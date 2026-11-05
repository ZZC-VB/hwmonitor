Imports System.Diagnostics
Imports System.Management
Imports System.Net.NetworkInformation
Imports System.Runtime.InteropServices
Imports System.Text.RegularExpressions
Imports LibreHardwareMonitor.Hardware
Imports Microsoft.Win32

''' <summary>
''' 采集层。口径取自参考文档 §3/§4：
'''   内存用量 → GlobalMemoryStatusEx；CPU 频率 / 硬盘活动与读写 → 系统性能计数器；
'''   CPU 占用与温度、GPU 全部、内存条与硬盘温度 → LibreHardwareMonitorLib。
''' 传感器匹配不到一律返回 Nothing，绝不抛异常、绝不当 0。
''' </summary>
Friend NotInheritable Class HardwareCollector
    Implements IDisposable

#Region "Win32"
    <StructLayout(LayoutKind.Sequential)>
    Private Structure MEMORYSTATUSEX
        Public dwLength As UInteger
        Public dwMemoryLoad As UInteger
        Public ullTotalPhys As ULong
        Public ullAvailPhys As ULong
        Public ullTotalPageFile As ULong
        Public ullAvailPageFile As ULong
        Public ullTotalVirtual As ULong
        Public ullAvailVirtual As ULong
        Public ullAvailExtendedVirtual As ULong
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GlobalMemoryStatusEx(ByRef lpBuffer As MEMORYSTATUSEX) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function
#End Region

    Private _computer As Computer
    Private ReadOnly _gate As New Object()
    Private _opened As Boolean

    Private _cpuUtil As PerformanceCounter   ' 任务管理器同款：% Processor Utility
    Private _cpuTime As PerformanceCounter   ' 旧系统兜底：% Processor Time
    Private _diskIdle As PerformanceCounter
    Private _diskRead As PerformanceCounter
    Private _diskWrite As PerformanceCounter
    Private _countersReady As Boolean
    Private ReadOnly _netPrev As New Dictionary(Of String, Long())()
    Private ReadOnly _netClock As Stopwatch = Stopwatch.StartNew()
    Private _netStampMs As Long = -1

    ' 型号是静态值：Open 时读一次缓存住，每帧只填字符串引用，不做任何额外采集
    Private _modelCpu As String = ""
    Private _modelGpu As String = ""
    Private _modelMem As String = ""

    ''' <summary>是否拿到了 CPU 温度（驱动 / 权限是否正常的粗略指示，托盘提示用）。</summary>
    Public Property HaveCpuTemp As Boolean

    Public Sub Open()
        SyncLock _gate
            CloseComputer()
            Try
                _computer = New Computer() With {
                    .IsCpuEnabled = True,
                    .IsGpuEnabled = True,
                    .IsMemoryEnabled = True,
                    .IsStorageEnabled = True,
                    .IsMotherboardEnabled = False,
                    .IsControllerEnabled = False,
                    .IsNetworkEnabled = False,
                    .IsBatteryEnabled = False,
                    .IsPsuEnabled = False,
                    .IsPowerMonitorEnabled = False
                }
                _computer.Open()
                _opened = True
                CacheModels()
            Catch
                _opened = False
            End Try
        End SyncLock
        If Not _countersReady Then InitCounters()

        ' 预热网络基准：速率靠前后两帧差值计算，不做这一步第一帧只能输出 null
        Try : FillNetwork(New Snapshot.NetInfo()) : Catch : End Try
    End Sub

    Private Sub CloseComputer()
        If _computer IsNot Nothing Then
            Try : _computer.Close() : Catch : End Try
            _computer = Nothing
        End If
        _opened = False
    End Sub

    Private Sub InitCounters()
        _countersReady = True
        _cpuUtil = MakeCounter("Processor Information", "% Processor Utility", "_Total")
        _cpuTime = MakeCounter("Processor Information", "% Processor Time", "_Total")
        _diskIdle = MakeCounter("PhysicalDisk", "% Idle Time", "_Total")
        _diskRead = MakeCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total")
        _diskWrite = MakeCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total")
    End Sub

    ''' <summary>建一次、每帧只 NextValue()；首次读数先丢弃以建立基准。</summary>
    Private Shared Function MakeCounter(cat As String, name As String, inst As String) As PerformanceCounter
        Try
            Dim pc As New PerformanceCounter(cat, name, inst, True)
            pc.NextValue()
            Return pc
        Catch
            Return Nothing
        End Try
    End Function

    Private Shared Function ReadCounter(pc As PerformanceCounter) As Double?
        If pc Is Nothing Then Return Nothing
        Try
            Dim v As Double = pc.NextValue()
            If Double.IsNaN(v) OrElse Double.IsInfinity(v) Then Return Nothing
            Return v
        Catch
            Return Nothing
        End Try
    End Function

#Region "型号（静态，只在 Open 时读一次）"
    ''' <summary>
    ''' 读一次 CPU / GPU / 内存型号并转成简短形式。型号不会变，没必要每帧读，
    ''' 每帧只是把字符串引用填进快照，开销可忽略。
    ''' </summary>
    Private Sub CacheModels()
        Try
            Dim bestGpu As IHardware = Nothing
            Dim bestRank As Integer = Integer.MaxValue
            For Each hw As IHardware In _computer.Hardware
                Select Case hw.HardwareType
                    Case HardwareType.Cpu
                        If _modelCpu.Length = 0 Then _modelCpu = ShortCpu(hw.Name)
                    Case HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel
                        Dim r As Integer = GpuRank(hw.HardwareType)
                        If r < bestRank Then
                            bestRank = r
                            bestGpu = hw
                        End If
                End Select
            Next
            If bestGpu IsNot Nothing Then _modelGpu = ShortGpu(bestGpu.Name)
        Catch
        End Try

        Try
            _modelMem = ReadMemoryModel()
        Catch
        End Try
    End Sub

    ' 只在「整段匹配」时丢弃，避免把 i7-12700K 这类型号误伤
    Private Shared ReadOnly CpuNoise As String() = {
        "intel", "amd", "core", "cpu", "processor", "gen", "with", "radeon"}
    Private Shared ReadOnly GpuNoise As String() = {
        "nvidia", "geforce", "amd", "radeon", "intel", "graphics", "ltd",
        "corporation", "inc", "corporation."}

    ''' <summary>"Intel Core Ultra 5 225F" → "Ultra 5 225F"；"12th Gen Intel(R) Core(TM) i5-12400" → "i5-12400"。</summary>
    Private Shared Function ShortCpu(raw As String) As String
        If String.IsNullOrEmpty(raw) Then Return ""
        Dim s As String = Regex.Replace(raw, "\((R|TM)\)", "", RegexOptions.IgnoreCase)
        Dim keep As New List(Of String)()
        For Each t As String In s.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim tl As String = t.ToLowerInvariant()
            If Array.IndexOf(CpuNoise, tl) >= 0 Then Continue For
            ' 12th / 3rd 这类世代前缀
            If Regex.IsMatch(t, "^\d+(st|nd|rd|th)$", RegexOptions.IgnoreCase) Then Continue For
            keep.Add(t)
        Next
        Dim r As String = String.Join(" ", keep).Trim()
        If r.Length = 0 Then r = s.Trim()
        Return r
    End Function

    ''' <summary>"NVIDIA GeForce RTX 5070" → "RTX 5070"；"AMD Radeon RX 7800 XT" → "RX 7800 XT"。</summary>
    Private Shared Function ShortGpu(raw As String) As String
        If String.IsNullOrEmpty(raw) Then Return ""
        Dim s As String = Regex.Replace(raw, "\((R|TM)\)", "", RegexOptions.IgnoreCase)
        Dim keep As New List(Of String)()
        For Each t As String In s.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
            If Array.IndexOf(GpuNoise, t.ToLowerInvariant()) >= 0 Then Continue For
            keep.Add(t)
        Next
        Dim r As String = String.Join(" ", keep).Trim()
        If r.Length = 0 Then Return "核显"
        Return r
    End Function

    ''' <summary>
    ''' "DDR5 6000"。走 WMI 而不是自己解析 SMBIOS：本机主板把 Type 17 的
    ''' Speed 字段写偏了一个字节（0x14 读出 28672），WMI 报的才是真实值。
    ''' 只在启动时查一次，约几十毫秒，不影响每帧开销。
    ''' </summary>
    Private Shared Function ReadMemoryModel() As String
        Try
            Dim typeName As String = ""
            Dim speed As Integer = 0
            Using searcher As New ManagementObjectSearcher(
                    "SELECT SMBIOSMemoryType, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory")
                For Each mo As ManagementBaseObject In searcher.Get()
                    If typeName.Length = 0 Then typeName = MemTypeName(ToInt(mo("SMBIOSMemoryType")))
                    Dim sp As Integer = ToInt(mo("ConfiguredClockSpeed"))
                    If sp <= 0 Then sp = ToInt(mo("Speed"))
                    If sp >= 1000 AndAlso sp <= 12000 AndAlso sp > speed Then speed = sp
                Next
            End Using
            If typeName.Length = 0 Then Return ""
            If speed > 0 Then Return typeName & " " & speed.ToString()
            Return typeName
        Catch
        End Try
        Return ""
    End Function

    Private Shared Function MemTypeName(t As Integer) As String
        Select Case t
            Case 20 : Return "DDR"
            Case 21 : Return "DDR2"
            Case 24 : Return "DDR3"
            Case 26 : Return "DDR4"
            Case 27 : Return "LPDDR"
            Case 28 : Return "LPDDR2"
            Case 29 : Return "LPDDR3"
            Case 30 : Return "LPDDR4"
            Case 34 : Return "DDR5"
            Case 35 : Return "LPDDR5"
        End Select
        Return ""
    End Function

    Private Shared Function ToInt(v As Object) As Integer
        If v Is Nothing Then Return 0
        Try
            Return Convert.ToInt32(v)
        Catch
            Return 0
        End Try
    End Function
#End Region

#Region "Sample"
    Public Function Sample() As Snapshot
        Dim snap As New Snapshot()

        ' 不依赖 LHM / 驱动 / 管理员的部分先填
        FillMemorySystem(snap.Mem)
        Dim idle As Double? = ReadCounter(_diskIdle)
        Dim rd As Double? = ReadCounter(_diskRead)
        Dim wr As Double? = ReadCounter(_diskWrite)
        Dim activity As Double? = Nothing
        If idle.HasValue Then activity = Math.Max(0, Math.Min(100, 100 - idle.Value))

        Dim bestGpuRank As Integer = Integer.MaxValue

        SyncLock _gate
            If _opened AndAlso _computer IsNot Nothing Then
                For Each hw As IHardware In _computer.Hardware
                    Try
                        UpdateDeep(hw)
                        Select Case hw.HardwareType
                            Case HardwareType.Cpu
                                FillCpu(hw, snap.Cpu)
                            Case HardwareType.Memory
                                If hw.Name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0 Then
                                    FillMemoryTemp(hw, snap.Mem)
                                End If
                            Case HardwareType.GpuNvidia, HardwareType.GpuAmd, HardwareType.GpuIntel
                                Dim rank As Integer = GpuRank(hw.HardwareType)
                                If rank < bestGpuRank Then
                                    bestGpuRank = rank
                                    snap.Gpu = New Snapshot.GpuInfo()
                                    FillGpu(hw, snap.Gpu)
                                End If
                            Case HardwareType.Storage
                                Dim d As New Snapshot.DiskInfo()
                                d.Name = Truncate(hw.Name, 28)
                                d.Temp = DiskTempOf(hw)
                                snap.Disks.Add(d)
                        End Select
                    Catch
                        ' 单个硬件失败只影响该硬件
                    End Try
                Next
            End If
        End SyncLock

        ' 型号统一在锁外填：上面可能用 New Snapshot.GpuInfo() 重建过 GPU 对象，早填会被覆盖
        snap.Cpu.Model = _modelCpu
        snap.Gpu.Model = _modelGpu
        snap.Mem.Model = _modelMem

        snap.Disks.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.Ordinal))
        ' PDH 用的是 _Total，所有物理盘汇总；单盘机器等价。多盘时每盘显示同一合计，所以只保留第一块。
        If snap.Disks.Count = 0 Then
            snap.Disks.Add(New Snapshot.DiskInfo() With {.Name = "Disk (Total)"})
        ElseIf snap.Disks.Count > 1 Then
            snap.Disks.RemoveRange(1, snap.Disks.Count - 1)
        End If
        Dim dk As Snapshot.DiskInfo = snap.Disks(0)
        dk.Activity = activity
        dk.ReadRate = rd
        dk.WriteRate = wr

        ' CPU 使用率：与任务管理器一致（Processor Utility，已按频率折算；任务管理器显示时封顶 100）。
        ' 读不到再退回 Processor Time，最后才用 LHM 的值。
        Dim cu As Double? = ReadCounter(_cpuUtil)
        If Not cu.HasValue Then cu = ReadCounter(_cpuTime)
        If cu.HasValue Then snap.Cpu.Load = Math.Max(0, Math.Min(100, cu.Value))

        FillNetwork(snap.Net)

        HaveCpuTemp = snap.Cpu.Temp.HasValue
        Return snap
    End Function

    Private Shared Function GpuRank(t As HardwareType) As Integer
        Select Case t
            Case HardwareType.GpuNvidia : Return 0
            Case HardwareType.GpuAmd : Return 1
            Case Else : Return 2
        End Select
    End Function

    Private Shared Function Truncate(s As String, n As Integer) As String
        If s Is Nothing Then Return ""
        Return If(s.Length <= n, s, s.Substring(0, n))
    End Function

    Private Shared Sub UpdateDeep(hw As IHardware)
        hw.Update()
        For Each sub1 As IHardware In hw.SubHardware
            UpdateDeep(sub1)
        Next
    End Sub

    Private Shared Sub FillMemorySystem(m As Snapshot.MemInfo)
        Dim ms As New MEMORYSTATUSEX()
        ms.dwLength = CUInt(Marshal.SizeOf(GetType(MEMORYSTATUSEX)))
        Try
            If GlobalMemoryStatusEx(ms) AndAlso ms.ullTotalPhys > 0 Then
                Dim used As ULong = ms.ullTotalPhys - ms.ullAvailPhys
                m.Load = used / CDbl(ms.ullTotalPhys) * 100.0
                m.Used = used / 1073741824.0
                m.Total = ms.ullTotalPhys / 1073741824.0
            End If
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' 网络速率：只统计「真的连着」的网卡（存在有默认网关的网卡时只算这些），
    ''' 字节计数增量 / 时间。不依赖性能计数器实例名，与系统语言无关。
    ''' </summary>
    Private Sub FillNetwork(n As Snapshot.NetInfo)
        Dim nowMs As Long = _netClock.ElapsedMilliseconds
        Dim dt As Double = If(_netStampMs < 0, 0.0, (nowMs - _netStampMs) / 1000.0)
        Dim sent As Double = 0
        Dim recv As Double = 0
        Dim cur As New Dictionary(Of String, Long())()
        Dim counted As Integer = 0

        Try
            For Each ni As NetworkInterface In NetworkUtil.GetActiveAdapters()
                Try
                    Dim st As IPv4InterfaceStatistics = ni.GetIPv4Statistics()
                    Dim vals As Long() = {st.BytesSent, st.BytesReceived}
                    cur(ni.Id) = vals

                    Dim prev As Long() = Nothing
                    If dt > 0.2 AndAlso _netPrev.TryGetValue(ni.Id, prev) Then
                        sent += Delta32(prev(0), vals(0))
                        recv += Delta32(prev(1), vals(1))
                        counted += 1
                    End If
                Catch
                End Try
            Next
        Catch
        End Try

        _netPrev.Clear()
        For Each kv As KeyValuePair(Of String, Long()) In cur
            _netPrev(kv.Key) = kv.Value
        Next
        _netStampMs = nowMs

        ' counted = 0 说明这是首帧、或网卡刚刚变化还没有基准：输出 null（客户端显示 --），
        ' 不要输出 0，否则「没有基准」会被误读成「确实没流量」。
        If dt > 0.2 AndAlso counted > 0 Then
            n.Sent = sent / dt
            n.Recv = recv / dt
        End If
    End Sub

    ''' <summary>
    ''' 32 位字节计数器的增量。NetworkInterface.GetIPv4Statistics 在 .NET Framework 上
    ''' 底层是 MIB_IFROW 的 DWORD 字段，累计超过 4 GB 会回绕成小数值。
    ''' 原来的 `If now >= prev` 写法在回绕后会永久丢弃增量——长期开机的电脑因此一直读不到速率。
    ''' </summary>
    Private Shared Function Delta32(prev As Long, now As Long) As Double
        Dim d As Long = now - prev
        If d < 0 Then
            If prev <= &HFFFFFFFFL Then
                d += &H100000000L          ' 2^32：回绕补偿
            Else
                Return 0                   ' 已是 64 位计数却变小，属异常，丢弃这一帧
            End If
        End If
        If d < 0 OrElse d > &HFFFFFFFFL Then Return 0
        Return d
    End Function
#End Region

#Region "Fill*"
    Private Shared Sub FillCpu(hw As IHardware, c As Snapshot.CpuInfo)
        c.Load = LoadOf(hw, True, "CPU Total", "CPU Core Max", "Total")
        c.Temp = TempOf(hw, "CPU Package", "Core Max", "Core Average", "CCD1 (Tdie)", "Core (Tctl/Tdie)", "Tdie", "Tctl")
    End Sub

    Private Shared Sub FillMemoryTemp(hw As IHardware, m As Snapshot.MemInfo)
        ' SPD 里混着 High Limit / Critical Limit / Resolution 等同为 Temperature 类型的常量，
        ' 只认名字以 DIMM 开头的，多条取最大值。
        Dim best As Double? = Nothing
        For Each s As ISensor In AllSensors(hw)
            If s.SensorType <> SensorType.Temperature Then Continue For
            If Not s.Name.StartsWith("DIMM", StringComparison.OrdinalIgnoreCase) Then Continue For
            If Not s.Value.HasValue Then Continue For
            Dim v As Double = s.Value.Value
            If v > 0 AndAlso v < 120 Then
                If Not best.HasValue OrElse v > best.Value Then best = v
            End If
        Next
        If best.HasValue Then m.Temp = best
    End Sub

    Private Shared Sub FillGpu(hw As IHardware, g As Snapshot.GpuInfo)
        g.Load = LoadOf(hw, True, "GPU Core", "D3D 3D", "GPU Total")
        g.Temp = TempOf(hw, "GPU Core", "GPU Temperature", "GPU Diode", "Edge")
        g.MemTemp = TempOf(hw, "GPU Memory Junction", "GPU Memory", "VRAM", "Memory Junction")
        g.MemUsed = DataOf(hw, "GPU Memory Used", "D3D Dedicated Memory Used", "Memory Used")
        g.MemTotal = DataOf(hw, "GPU Memory Total", "D3D Dedicated Memory Total", "Memory Total")

        ' 单位自愈：不同卡 / 不同 LHM 版本给 MB 或 GB 不统一
        If g.MemTotal.HasValue AndAlso g.MemTotal.Value < 1000 Then
            g.MemTotal = g.MemTotal.Value * 1024
            If g.MemUsed.HasValue AndAlso g.MemUsed.Value <= g.MemTotal.Value / 1024 * 1.05 Then
                g.MemUsed = g.MemUsed.Value * 1024
            End If
        ElseIf g.MemTotal.HasValue AndAlso g.MemUsed.HasValue AndAlso g.MemUsed.Value > g.MemTotal.Value * 1.05 Then
            g.MemUsed = g.MemUsed.Value / 1024
        End If
    End Sub
#End Region

#Region "传感器匹配"
    Private Shared Iterator Function AllSensors(hw As IHardware) As IEnumerable(Of ISensor)
        For Each s As ISensor In hw.Sensors
            Yield s
        Next
        For Each sh As IHardware In hw.SubHardware
            For Each s As ISensor In AllSensors(sh)
                Yield s
            Next
        Next
    End Function

    Private Shared Function Named(hw As IHardware, type As SensorType, name As String) As ISensor
        For Each s As ISensor In AllSensors(hw)
            If s.SensorType = type AndAlso String.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) Then
                If s.Value.HasValue Then Return s
            End If
        Next
        Return Nothing
    End Function

    ''' <summary>按名字优先级精确匹配；fallbackFirst 时全不命中则取第一个 Load。</summary>
    Private Shared Function LoadOf(hw As IHardware, fallbackFirst As Boolean, ParamArray names() As String) As Double?
        For Each n As String In names
            Dim s As ISensor = Named(hw, SensorType.Load, n)
            If s IsNot Nothing Then Return s.Value.Value
        Next
        If fallbackFirst Then
            For Each s As ISensor In AllSensors(hw)
                If s.SensorType = SensorType.Load AndAlso s.Value.HasValue Then Return s.Value.Value
            Next
        End If
        Return Nothing
    End Function

    ''' <summary>无兜底：避免把阈值 / 热点当核心温度。</summary>
    Private Shared Function TempOf(hw As IHardware, ParamArray names() As String) As Double?
        For Each n As String In names
            Dim s As ISensor = Named(hw, SensorType.Temperature, n)
            If s IsNot Nothing Then
                Dim v As Double = s.Value.Value
                If v > 0 AndAlso v < 150 Then Return v
            End If
        Next
        Return Nothing
    End Function

    Private Shared Function DataOf(hw As IHardware, ParamArray names() As String) As Double?
        For Each n As String In names
            Dim s As ISensor = Named(hw, SensorType.Data, n)
            If s Is Nothing Then s = Named(hw, SensorType.SmallData, n)
            If s IsNot Nothing AndAlso s.Value.Value >= 0 Then Return s.Value.Value
        Next
        Return Nothing
    End Function

    ''' <summary>NVMe 的温度列表里混着 Warning / Critical / Resolution 等阈值常量，必须排除。</summary>
    Private Shared Function DiskTempOf(hw As IHardware) As Double?
        Dim composite As Double? = Nothing
        Dim plain As Double? = Nothing
        Dim firstOk As Double? = Nothing
        For Each s As ISensor In AllSensors(hw)
            If s.SensorType <> SensorType.Temperature OrElse Not s.Value.HasValue Then Continue For
            Dim nm As String = s.Name
            If nm.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0 _
               OrElse nm.IndexOf("Critical", StringComparison.OrdinalIgnoreCase) >= 0 _
               OrElse nm.IndexOf("Limit", StringComparison.OrdinalIgnoreCase) >= 0 _
               OrElse nm.IndexOf("Resolution", StringComparison.OrdinalIgnoreCase) >= 0 Then Continue For
            Dim v As Double = s.Value.Value
            If v <= 0 OrElse v >= 100 Then Continue For
            If Not firstOk.HasValue Then firstOk = v
            If String.Equals(nm, "Composite Temperature", StringComparison.OrdinalIgnoreCase) Then composite = v
            If String.Equals(nm, "Temperature", StringComparison.OrdinalIgnoreCase) Then plain = v
        Next
        If composite.HasValue Then Return composite
        If plain.HasValue Then Return plain
        Return firstOk
    End Function
#End Region

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock _gate
            CloseComputer()
        End SyncLock
        For Each pc As PerformanceCounter In New PerformanceCounter() {_cpuUtil, _cpuTime, _diskIdle, _diskRead, _diskWrite}
            If pc IsNot Nothing Then pc.Dispose()
        Next
    End Sub
End Class

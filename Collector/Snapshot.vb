Imports System.Text

''' <summary>一帧数据。Nothing = 该硬件不提供此指标，序列化为 null（客户端显示 "--"，不当 0）。</summary>
Friend NotInheritable Class Snapshot
    Public Class CpuInfo
        Public Load As Double?
        Public Temp As Double?
        ''' <summary>简短型号，如 "Ultra 5 225F"。空字符串表示没取到，序列化时直接不输出该字段。</summary>
        Public Model As String = ""
    End Class

    Public Class MemInfo
        Public Load As Double?
        Public Temp As Double?
        Public Used As Double?    ' GB
        Public Total As Double?   ' GB
        ''' <summary>简短型号，如 "DDR5 6000"。</summary>
        Public Model As String = ""
    End Class

    ''' <summary>
    ''' 只保留客户端屏幕上真正会画出来的量。显存占用率（可由 mu/mv 算出）、
    ''' 风扇、功耗客户端都不显示，已停止采集——每帧少读 3 个传感器。
    ''' </summary>
    Public Class GpuInfo
        Public Load As Double?
        Public Temp As Double?
        Public MemTemp As Double?
        Public MemUsed As Double?   ' MB
        Public MemTotal As Double?  ' MB
        ''' <summary>简短型号，如 "RTX 5070"。</summary>
        Public Model As String = ""
    End Class

    Public Class NetInfo
        Public Sent As Double?  ' B/s（所有可用网卡合计）
        Public Recv As Double?  ' B/s
    End Class

    Public Class DiskInfo
        Public Name As String = ""
        Public Temp As Double?
        Public Activity As Double?  ' 活动时间 %
        Public ReadRate As Double?  ' B/s
        Public WriteRate As Double? ' B/s
    End Class

    Public Cpu As New CpuInfo()
    Public Mem As New MemInfo()
    Public Gpu As New GpuInfo()
    Public Net As New NetInfo()
    Public Disks As New List(Of DiskInfo)()

    Public Function ToJson() As String
        Dim j As New JsonBuilder()
        j.OpenObject()
        j.Lng("ts", CLng((DateTime.UtcNow - New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds))

        ' 型号是静态值，取不到就整字段不输出（客户端按「字段缺失」处理，不要输出空串）
        j.OpenObject("cpu")
        j.Num("l", Cpu.Load) : j.Num("t", Cpu.Temp)
        If Not String.IsNullOrEmpty(Cpu.Model) Then j.Str("m", Cpu.Model)
        j.CloseObject()

        j.OpenObject("mem")
        j.Num("l", Mem.Load) : j.Num("t", Mem.Temp) : j.Num("u", Mem.Used) : j.Num("a", Mem.Total)
        If Not String.IsNullOrEmpty(Mem.Model) Then j.Str("m", Mem.Model)
        j.CloseObject()

        j.OpenObject("gpu")
        j.Num("l", Gpu.Load) : j.Num("t", Gpu.Temp)
        j.Num("mt", Gpu.MemTemp)
        j.Num("mu", Gpu.MemUsed) : j.Num("mv", Gpu.MemTotal)
        If Not String.IsNullOrEmpty(Gpu.Model) Then j.Str("m", Gpu.Model)
        j.CloseObject()

        j.OpenObject("net")
        j.Num("s", Net.Sent) : j.Num("r", Net.Recv)
        j.CloseObject()

        j.OpenArray("disks")
        For Each d As DiskInfo In Disks
            j.OpenObject()
            j.Str("n", d.Name)
            j.Num("t", d.Temp) : j.Num("u", d.Activity)
            j.Num("r", d.ReadRate) : j.Num("w", d.WriteRate)
            j.CloseObject()
        Next
        j.CloseArray()

        j.CloseObject()
        Return j.ToString()
    End Function
End Class

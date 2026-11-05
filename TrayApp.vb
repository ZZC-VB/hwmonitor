Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Security.Principal
Imports System.Text
Imports System.Threading
Imports System.Windows.Forms
Imports Microsoft.Win32

''' <summary>托盘常驻：无窗口、无配置、无日志。只做 采集 → 序列化 → 推送，外加退出。</summary>
Friend NotInheritable Class TrayApp
    Inherits ApplicationContext

    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function DestroyIcon(hIcon As IntPtr) As Boolean
    End Function

    Private ReadOnly _collector As HardwareCollector
    Private ReadOnly _server As MonitorServer
    Private ReadOnly _discovery As DiscoveryService
    Private ReadOnly _tray As NotifyIcon
    Private ReadOnly _addrItem As ToolStripMenuItem
    Private ReadOnly _statusItem As ToolStripMenuItem
    Private _lastError As String = Nothing
    Private ReadOnly _tipTimer As System.Windows.Forms.Timer
    Private ReadOnly _icon As Icon
    Private _hIcon As IntPtr
    Private _sampler As Thread
    Private _stop As Boolean
    Private _last As Snapshot
    Private _driverNote As String = ""
    Private _addrTick As Integer

    Public Sub New()
        _collector = New HardwareCollector()
        _collector.Open()
        EnsureDriver()
        EnsureFirewall()

        _server = New MonitorServer(Program.Port)
        _discovery = New DiscoveryService(Program.DiscoveryPort, Program.Port)

        ' 托盘菜单：地址 / 分隔线 / 退出
        Dim menu As New ContextMenuStrip()
        _addrItem = New ToolStripMenuItem("") With {.Enabled = False}
        menu.Items.Add(_addrItem)
        _statusItem = New ToolStripMenuItem("状态：启动中…") With {.Enabled = False}
        menu.Items.Add(_statusItem)
        menu.Items.Add(New ToolStripSeparator())
        Dim exitItem As New ToolStripMenuItem("退出")
        AddHandler exitItem.Click, AddressOf OnExit
        menu.Items.Add(exitItem)
        UpdateAddressText()

        ' 托盘用和 exe 同一个图标（app.ico 已作为程序集资源嵌入），
        ' 实在取不到才回退到代码绘制的那版
        _icon = LoadAppIcon()
        If _icon Is Nothing Then _icon = MakeIcon(_hIcon)
        _tray = New NotifyIcon() With {
            .Icon = _icon,
            .Text = "硬件监控",
            .ContextMenuStrip = menu,
            .Visible = True
        }

        _server.Start()
        Try : _discovery.Start() : Catch : End Try

        _sampler = New Thread(AddressOf SampleLoop)
        _sampler.IsBackground = True
        _sampler.Name = "sampler"
        _sampler.Priority = ThreadPriority.BelowNormal
        _sampler.Start()

        _tipTimer = New System.Windows.Forms.Timer() With {.Interval = 1000}
        AddHandler _tipTimer.Tick, AddressOf OnTipTick
        _tipTimer.Start()
    End Sub

#Region "驱动自举"
    Private Shared Function IsAdmin() As Boolean
        Try
            Using id As WindowsIdentity = WindowsIdentity.GetCurrent()
                Return New WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)
            End Using
        Catch
            Return False
        End Try
    End Function

    Private Shared Function PawnIoInstalled() As Boolean
        Try
            Using k As RegistryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\PawnIO")
                Return k IsNot Nothing
            End Using
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' CPU 温度要读 MSR，需要 ring0 驱动。照 CPU-Z 的做法代劳：缺 PawnIO 就静默安装，
    ''' 装完必须重新 Open()（LHM 在启动时缓存 MSR 可用性，不重开不会生效）。
    ''' </summary>
    Private Sub EnsureDriver()
        If Not IsAdmin() Then
            _driverNote = " · 需管理员权限"
            Return
        End If
        If PawnIoInstalled() Then Return

        Dim setup As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PawnIO_setup.exe")
        If Not File.Exists(setup) Then
            _driverNote = " · 需安装 PawnIO"
            Return
        End If
        Try
            Dim psi As New ProcessStartInfo(setup, "-install -silent") With {
                .UseShellExecute = False,
                .CreateNoWindow = True
            }
            Using p As Process = Process.Start(psi)
                If p IsNot Nothing Then p.WaitForExit(60000)
            End Using
            _collector.Open()
        Catch
            _driverNote = " · 需安装 PawnIO"
        End Try
    End Sub
#End Region

#Region "防火墙"
    ''' <summary>
    ''' 管理员运行时自动放行入站 TCP 9455 / UDP 9456（规则名 HwMonitor，只加一次）。
    ''' 否则网络被识别为“公用网络”或首次弹窗被忽略时，手机会一直连不上。
    ''' </summary>
    Private Shared Sub EnsureFirewall()
        If Not IsAdmin() Then Return
        Try
            If RunNetsh("advfirewall firewall show rule name=""HwMonitor""") = 0 Then Return
            RunNetsh("advfirewall firewall add rule name=""HwMonitor"" dir=in action=allow protocol=TCP localport=" &
                     Program.Port.ToString() & " profile=any")
            RunNetsh("advfirewall firewall add rule name=""HwMonitor"" dir=in action=allow protocol=UDP localport=" &
                     Program.DiscoveryPort.ToString() & " profile=any")
        Catch
        End Try
    End Sub

    Private Shared Function RunNetsh(args As String) As Integer
        Dim psi As New ProcessStartInfo("netsh", args) With {
            .UseShellExecute = False,
            .CreateNoWindow = True,
            .RedirectStandardOutput = True
        }
        Using p As Process = Process.Start(psi)
            If p Is Nothing Then Return -1
            p.StandardOutput.ReadToEnd()
            p.WaitForExit(8000)
            Return p.ExitCode
        End Using
    End Function
#End Region

#Region "采样线程"
    Private Sub SampleLoop()
        Dim sw As Stopwatch = Stopwatch.StartNew()
        Dim nextTick As Long = 0
        Dim usedMs As Integer = Program.IntervalMs
        Do While Not _stop
            Try
                Dim snap As Snapshot = _collector.Sample()
                _last = snap
                Dim bytes As Byte() = Encoding.UTF8.GetBytes(snap.ToJson())
                _server.Broadcast(bytes)
                _lastError = Nothing
            Catch ex As Exception
                ' 一帧失败不影响下一帧；原因显示在托盘菜单状态行
                _lastError = ex.GetType().Name & ": " & ex.Message
            End Try

            ' 按绝对时间点对齐，而不是「睡固定时长」：Windows 的 Sleep 每次都会多睡几毫秒，
            ' 分片累积起来 60 秒能拖成 64 秒。绝对对齐后，上一帧多睡的会在下一帧补回来。
            nextTick += usedMs
            Dim nowMs As Long = sw.ElapsedMilliseconds
            If nextTick <= nowMs Then nextTick = nowMs + usedMs

            ' 分片等待：既为了尽快响应退出，也为了盯住间隔是否被手机改过。
            ' 以前这里只算时间不算间隔，60 秒改 1 秒得等满 60 秒才生效——这就是
            ' “设置已保存但两端还是 1 秒/还是旧节奏”的根因。
            Do While Not _stop
                If Program.IntervalMs <> usedMs Then Exit Do      ' 间隔变了：立刻回去采一帧
                Dim left As Long = nextTick - sw.ElapsedMilliseconds
                If left <= 0 Then Exit Do
                Thread.Sleep(CInt(Math.Min(left, 100L)))
            Loop

            If Not _stop AndAlso Program.IntervalMs <> usedMs Then
                usedMs = Program.IntervalMs
                nextTick = sw.ElapsedMilliseconds                  ' 重新对齐，不背旧节奏的欠账
            End If
        Loop
    End Sub
#End Region

#Region "托盘"
    Private Sub UpdateAddressText()
        _addrItem.Text = NetworkUtil.GetLocalIPv4() & ":" & Program.Port.ToString() & _driverNote
    End Sub

    Private Sub OnTipTick(sender As Object, e As EventArgs)
        ' 拔网线 / 切 Wi-Fi 后地址会变。每 5 秒对一次，内容没变就不改，避免菜单闪烁。
        _addrTick += 1
        If _addrTick >= 5 Then
            _addrTick = 0
            Dim addr As String = NetworkUtil.GetLocalIPv4() & ":" & Program.Port.ToString() & _driverNote
            If _addrItem.Text <> addr Then _addrItem.Text = addr
        End If

        ' 监听失败（端口被占用等）每秒重试一次，并把原因写在状态行
        If Not _server.IsListening Then _server.Start()
        If Not _server.IsListening Then
            _statusItem.Text = "状态：监听失败 " & _server.LastError
        ElseIf _lastError IsNot Nothing Then
            _statusItem.Text = "状态：采集异常 " & _lastError
        Else
            _statusItem.Text = "状态：正常，已连接手机 " & _server.ClientCount.ToString() &
                               " 台 · " & (Program.IntervalMs \ 1000).ToString() & "s"
        End If
        Dim s As Snapshot = _last
        If s Is Nothing Then Return
        Dim sb As New StringBuilder()
        sb.Append("CPU ").Append(Fmt(s.Cpu.Load, "0")).Append("% ").Append(Fmt(s.Cpu.Temp, "0")).Append("°C")
        sb.Append(" | GPU ").Append(Fmt(s.Gpu.Load, "0")).Append("% ").Append(Fmt(s.Gpu.Temp, "0")).Append("°C")
        sb.Append(" | 内存 ").Append(Fmt(s.Mem.Load, "0")).Append("%")
        Dim t As String = sb.ToString()
        If t.Length > 63 Then t = t.Substring(0, 63)     ' NotifyIcon.Text 上限 63 字符
        ' 内容没变就别赋值：每秒改一次会让 Shell 反复处理通知，白费一点开销
        If _tray.Text <> t Then _tray.Text = t
    End Sub

    Private Shared Function Fmt(v As Double?, f As String) As String
        Return If(v.HasValue, v.Value.ToString(f, Globalization.CultureInfo.InvariantCulture), "--")
    End Function

    ''' <summary>
    ''' 从程序集嵌入资源里取 app.ico，按系统托盘尺寸选帧（通常 16x16）。
    ''' 比 ExtractAssociatedIcon 准——后者只返回 32x32 位图，缩到 16 会糊。
    ''' 取不到返回 Nothing，由调用方回退到代码绘制。
    ''' </summary>
    Private Shared Function LoadAppIcon() As Icon
        Try
            Dim asm As System.Reflection.Assembly = System.Reflection.Assembly.GetExecutingAssembly()
            Using st As Stream = asm.GetManifestResourceStream("HwMonitor.app.ico")
                If st Is Nothing Then Return Nothing
                Using raw As New MemoryStream()
                    st.CopyTo(raw)
                    raw.Position = 0
                    Using tmp As New Icon(raw, SystemInformation.SmallIconSize)
                        Return CType(tmp.Clone(), Icon)   ' Clone 后可以安全关闭流
                    End Using
                End Using
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>代码绘制的绿色心电图标，无资源文件。仅在嵌入图标取不到时兜底。</summary>
    Private Shared Function MakeIcon(ByRef hIcon As IntPtr) As Icon
        Using bmp As New Bitmap(32, 32)
            Using g As Graphics = Graphics.FromImage(bmp)
                g.SmoothingMode = SmoothingMode.AntiAlias
                g.Clear(Color.Transparent)
                Using bg As New SolidBrush(Color.FromArgb(255, 11, 15, 23))
                    g.FillEllipse(bg, 1, 1, 30, 30)
                End Using
                Using pen As New Pen(Color.FromArgb(255, 53, 214, 160), 2.6F)
                    pen.LineJoin = LineJoin.Round
                    g.DrawLines(pen, New PointF() {
                        New PointF(4, 17), New PointF(11, 17), New PointF(14, 8),
                        New PointF(19, 25), New PointF(22, 14), New PointF(28, 14)})
                End Using
            End Using
            hIcon = bmp.GetHicon()
            Return Icon.FromHandle(hIcon)
        End Using
    End Function

    Private Sub OnExit(sender As Object, e As EventArgs)
        _stop = True
        _tipTimer.Stop()
        _tray.Visible = False
        Try : _server.Stop() : Catch : End Try
        Try : _discovery.Stop() : Catch : End Try
        Try : _sampler.Join(1500) : Catch : End Try
        Try : _collector.Dispose() : Catch : End Try
        _tray.Dispose()
        _icon.Dispose()
        If _hIcon <> IntPtr.Zero Then DestroyIcon(_hIcon)
        ExitThread()
    End Sub
#End Region
End Class

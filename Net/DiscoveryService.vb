Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

''' <summary>
''' UDP 自动发现（双路）：
'''   被动应答——收到任意非 HWST 包就单播回发现报文，手机进页面立刻能看到电脑；
'''   主动广播——每 3 s 向 255.255.255.255 和每个网卡的定向广播地址各发一次。
''' </summary>
Friend NotInheritable Class DiscoveryService
    Private ReadOnly _discoveryPort As Integer
    Private ReadOnly _tcpPort As Integer
    Private _udp As UdpClient
    Private _running As Boolean

    Public Sub New(discoveryPort As Integer, tcpPort As Integer)
        _discoveryPort = discoveryPort
        _tcpPort = tcpPort
    End Sub

    Private Function BuildPacket() As Byte()
        Dim name As String = Environment.MachineName.Replace("\", "").Replace("""", "")
        Dim json As String = "{""m"":""HWST"",""v"":1,""n"":""" & name & """,""ip"":""" &
                             NetworkUtil.GetLocalIPv4() & """,""port"":" & _tcpPort.ToString() & "}"
        Return Encoding.UTF8.GetBytes(json)
    End Function

    Public Sub Start()
        Dim sock As New Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        sock.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, True)
        sock.EnableBroadcast = True
        sock.Bind(New IPEndPoint(IPAddress.Any, _discoveryPort))
        _udp = New UdpClient()
        _udp.Client = sock
        _running = True

        Dim rx As New Thread(AddressOf ReceiveLoop)
        rx.IsBackground = True : rx.Name = "udp-recv" : rx.Priority = ThreadPriority.BelowNormal
        rx.Start()

        Dim tx As New Thread(AddressOf BroadcastLoop)
        tx.IsBackground = True : tx.Name = "udp-bcast" : tx.Priority = ThreadPriority.Lowest
        tx.Start()
    End Sub

    Private Sub ReceiveLoop()
        Do While _running
            Try
                Dim remote As New IPEndPoint(IPAddress.Any, 0)
                Dim data As Byte() = _udp.Receive(remote)
                ' 自己发出的广播也会被自己收到：HWST 报文以 { 开头，直接忽略
                If data.Length > 0 AndAlso data(0) = AscW("{"c) Then Continue Do
                Dim pkt As Byte() = BuildPacket()
                _udp.Send(pkt, pkt.Length, remote)
            Catch
                If Not _running Then Exit Do
                Thread.Sleep(100)
            End Try
        Loop
    End Sub

    Private Sub BroadcastLoop()
        Do While _running
            Try
                Dim pkt As Byte() = BuildPacket()
                _udp.Send(pkt, pkt.Length, New IPEndPoint(IPAddress.Broadcast, _discoveryPort))
                For Each a As IPAddress In NetworkUtil.GetBroadcastAddresses()
                    Try
                        _udp.Send(pkt, pkt.Length, New IPEndPoint(a, _discoveryPort))
                    Catch
                    End Try
                Next
            Catch
            End Try
            For i As Integer = 1 To 30
                If Not _running Then Exit Do
                Thread.Sleep(100)
            Next
        Loop
    End Sub

    Public Sub [Stop]()
        _running = False
        Try : _udp.Close() : Catch : End Try
    End Sub
End Class

Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading

''' <summary>
''' TCP 推流。每个连接一个 8 帧的有界队列，满了丢最旧——广播永远不会阻塞采集线程。
''' 客户端不需要发送任何数据；Recv 线程只用来感知断开。
''' </summary>
Friend NotInheritable Class MonitorServer
    Private NotInheritable Class ClientConn
        Private ReadOnly _tcp As TcpClient
        Private ReadOnly _queue As New BlockingCollection(Of Byte())(8)
        Private ReadOnly _onClosed As Action(Of ClientConn)
        Private _closed As Integer

        Public Sub New(tcp As TcpClient, onClosed As Action(Of ClientConn))
            _tcp = tcp
            _onClosed = onClosed
            _tcp.NoDelay = True          ' 必开：否则 Nagle 会把 1 Hz 小包攒批，手机上一顿一顿
            _tcp.SendTimeout = 5000
            StartThread(AddressOf SendLoop, "tcp-send")
            StartThread(AddressOf RecvLoop, "tcp-recv")
        End Sub

        Private Shared Sub StartThread(body As ThreadStart, name As String)
            Dim t As New Thread(body)
            t.IsBackground = True
            t.Name = name
            t.Priority = ThreadPriority.BelowNormal
            t.Start()
        End Sub

        Public Sub Enqueue(payload As Byte())
            If _closed <> 0 Then Return
            If Not _queue.TryAdd(payload) Then
                Dim dropped As Byte() = Nothing
                _queue.TryTake(dropped)
                _queue.TryAdd(payload)
            End If
        End Sub

        Private Sub SendLoop()
            Try
                Dim ns As NetworkStream = _tcp.GetStream()
                For Each payload As Byte() In _queue.GetConsumingEnumerable()
                    ns.Write(payload, 0, payload.Length)
                    ns.WriteByte(10)    ' '\n'
                    ns.Flush()
                Next
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>
        ''' 客户端只发命令（采样间隔），按 NDJSON 逐行解析。
        ''' 不解析的行直接丢掉，绝不因为收到杂数据就断开连接。
        ''' </summary>
        Private Sub RecvLoop()
            Try
                Dim ns As NetworkStream = _tcp.GetStream()
                Dim buf(511) As Byte
                Dim pending As New StringBuilder()
                Do
                    Dim n As Integer = ns.Read(buf, 0, buf.Length)
                    If n <= 0 Then Exit Do
                    pending.Append(Encoding.UTF8.GetString(buf, 0, n))
                    Do
                        Dim line As String = TakeLine(pending)
                        If line Is Nothing Then Exit Do
                        If line.Length > 0 Then ApplyCommand(line)
                    Loop
                Loop
            Catch
            End Try
            Close()
        End Sub

        ''' <summary>从缓冲头部取出一整行（不含 \n）；还没有换行就返回 Nothing，等下次 Read。</summary>
        Private Shared Function TakeLine(sb As StringBuilder) As String
            Dim s As String = sb.ToString()
            Dim i As Integer = s.IndexOf(ControlChars.Lf)
            If i < 0 Then Return Nothing
            sb.Clear()
            If i + 1 < s.Length Then sb.Append(s.Substring(i + 1))
            Return s.Substring(0, i).Trim()
        End Function

        ''' <summary>
        ''' 只认 {"c":"interval","v":秒}。手写解析，不引 JSON 库——报文是我们自己的客户端发的，格式受控。
        ''' 多台手机同时连时，以最后收到的设置为准。
        ''' </summary>
        Private Shared Sub ApplyCommand(line As String)
            Try
                If line.IndexOf("""interval""", StringComparison.OrdinalIgnoreCase) < 0 Then Return

                Dim key As Integer = line.IndexOf("""v""", StringComparison.Ordinal)
                If key < 0 Then Return
                Dim colon As Integer = line.IndexOf(":"c, key)
                If colon < 0 Then Return

                Dim digits As New StringBuilder()
                For i As Integer = colon + 1 To line.Length - 1
                    Dim ch As Char = line(i)
                    If Char.IsDigit(ch) Then
                        digits.Append(ch)
                    ElseIf digits.Length > 0 Then
                        Exit For
                    End If
                Next

                Dim sec As Integer
                If Integer.TryParse(digits.ToString(), sec) AndAlso sec >= 1 AndAlso sec <= 60 Then
                    Program.IntervalMs = sec * 1000
                End If
            Catch
            End Try
        End Sub

        Public Sub Close()
            If Interlocked.Exchange(_closed, 1) <> 0 Then Return
            Try : _queue.CompleteAdding() : Catch : End Try
            Try : _tcp.Close() : Catch : End Try
            _onClosed(Me)
        End Sub
    End Class

    Private ReadOnly _port As Integer
    Private ReadOnly _clients As New List(Of ClientConn)()
    Private ReadOnly _lock As New Object()
    Private _listener As TcpListener
    Private _running As Boolean
    Private _lastPayload As Byte() = Nothing

    ''' <summary>是否正在监听（端口被占用 / 被安全软件拦截时为 False，LastError 有原因）。</summary>
    Public Property IsListening As Boolean
    Public Property LastError As String

    Public ReadOnly Property ClientCount As Integer
        Get
            SyncLock _lock
                Return _clients.Count
            End SyncLock
        End Get
    End Property

    Public Sub New(port As Integer)
        _port = port
    End Sub

    Public Function Start() As Boolean
        Try
            If _listener IsNot Nothing Then
                Try : _listener.Stop() : Catch : End Try
            End If
            _listener = New TcpListener(IPAddress.Any, _port)
            _listener.Start(16)
        Catch ex As Exception
            IsListening = False
            LastError = ex.Message
            Return False
        End Try
        LastError = Nothing
        IsListening = True
        _running = True
        Dim t As New Thread(AddressOf AcceptLoop)
        t.IsBackground = True
        t.Name = "tcp-accept"
        t.Priority = ThreadPriority.BelowNormal
        t.Start()
        Return True
    End Function

    Private Sub AcceptLoop()
        Do While _running
            Try
                Dim c As TcpClient = _listener.AcceptTcpClient()
                Dim conn As New ClientConn(c, AddressOf OnClientClosed)
                SyncLock _lock
                    _clients.Add(conn)
                End SyncLock
                ' 新连接立刻补发最近一帧，不用等下一个采样周期
                Dim last As Byte() = _lastPayload
                If last IsNot Nothing Then conn.Enqueue(last)
            Catch
                If Not _running Then Exit Do
                Thread.Sleep(200)
            End Try
        Loop
    End Sub

    Private Sub OnClientClosed(c As ClientConn)
        SyncLock _lock
            _clients.Remove(c)
        End SyncLock
    End Sub

    Public Sub Broadcast(payload As Byte())
        _lastPayload = payload
        Dim snapshot As ClientConn()
        SyncLock _lock
            If _clients.Count = 0 Then Return      ' 没人连就别分配数组，这是每帧都会走到的路径
            snapshot = _clients.ToArray()
        End SyncLock
        For Each c As ClientConn In snapshot
            c.Enqueue(payload)
        Next
    End Sub

    Public Sub [Stop]()
        _running = False
        Try : _listener.Stop() : Catch : End Try
        Dim snapshot As ClientConn()
        SyncLock _lock
            snapshot = _clients.ToArray()
        End SyncLock
        For Each c As ClientConn In snapshot
            c.Close()
        Next
    End Sub
End Class

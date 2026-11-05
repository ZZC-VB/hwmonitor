Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets

''' <summary>
''' 网卡判定。原则：只认「真的连着」的网卡——链路 Up 且拿到可用 IPv4。
''' 名字里带 virtual/vpn/tun 的只是辅助排除，不能当唯一依据（不同厂商描述千奇百怪，
''' 靠关键词一刀切会把真实网卡误杀，导致多网卡机器上网络速率一直取不到值）。
''' </summary>
Friend NotInheritable Class NetworkUtil
    Private Sub New()
    End Sub

    ''' <summary>明确的虚拟 / 隧道 / 代理网卡。命中即排除，即使它有默认网关（VPN 会造成流量重复计算）。</summary>
    Private Shared ReadOnly VirtualKeys As String() = {
        "vmware", "virtualbox", "hyper-v", "vethernet", "tap-", "tap_",
        "wsl", "loopback", "pseudo", "wireguard", "openvpn", "zerotier",
        "tailscale", "clash", "sing-box", "hamachi", "radmin", "npcap",
        "virtual adapter", "vpn client"}

    Private Shared Function IsVirtual(ni As NetworkInterface) As Boolean
        Dim s As String = (ni.Name & " " & ni.Description).ToLowerInvariant()
        For Each k As String In VirtualKeys
            If s.Contains(k) Then Return True
        Next
        Return False
    End Function

    Private Shared Function IsGoodV4(a As IPAddress) As Boolean
        If a Is Nothing Then Return False
        If a.AddressFamily <> AddressFamily.InterNetwork Then Return False
        Dim b() As Byte = a.GetAddressBytes()
        If b(0) = 127 Then Return False
        If b(0) = 169 AndAlso b(1) = 254 Then Return False    ' APIPA：没拿到 DHCP，等于没连上
        If b(0) = 0 Then Return False
        Return True
    End Function

    Private Shared Function FirstIPv4(ni As NetworkInterface) As IPAddress
        Try
            For Each ua As UnicastIPAddressInformation In ni.GetIPProperties().UnicastAddresses
                If IsGoodV4(ua.Address) Then Return ua.Address
            Next
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>「连上了」的硬判据：链路 Up + 有非 127/169.254 的 IPv4 + 不是虚拟/隧道网卡。</summary>
    Public Shared Function IsUsable(ni As NetworkInterface) As Boolean
        Try
            If ni.OperationalStatus <> OperationalStatus.Up Then Return False
            If ni.NetworkInterfaceType = NetworkInterfaceType.Loopback Then Return False
            If ni.NetworkInterfaceType = NetworkInterfaceType.Tunnel Then Return False
            If IsVirtual(ni) Then Return False
            Return FirstIPv4(ni) IsNot Nothing
        Catch
            Return False
        End Try
    End Function

    ''' <summary>有 IPv4 默认网关——真正承载上网流量的网卡才有。内网专线、虚拟链路通常没有。</summary>
    Public Shared Function HasGateway(ni As NetworkInterface) As Boolean
        Try
            For Each gw As GatewayIPAddressInformation In ni.GetIPProperties().GatewayAddresses
                If gw.Address IsNot Nothing AndAlso
                   gw.Address.AddressFamily = AddressFamily.InterNetwork AndAlso
                   Not gw.Address.Equals(IPAddress.Any) Then
                    Return True
                End If
            Next
        Catch
        End Try
        Return False
    End Function

    ''' <summary>
    ''' 参与统计的网卡：已连接的全算；但如果其中有「已连接且有默认网关」的，
    ''' 就只算这些——多网卡时避免把内网链路、第二块没上网的网卡算进来。
    ''' </summary>
    Public Shared Function GetActiveAdapters() As List(Of NetworkInterface)
        Dim all As New List(Of NetworkInterface)()
        Dim withGateway As New List(Of NetworkInterface)()
        Try
            For Each ni As NetworkInterface In NetworkInterface.GetAllNetworkInterfaces()
                If Not IsUsable(ni) Then Continue For
                all.Add(ni)
                If HasGateway(ni) Then withGateway.Add(ni)
            Next
        Catch
        End Try
        If withGateway.Count > 0 Then Return withGateway
        Return all
    End Function

    ''' <summary>本机 IPv4：优先有默认网关 -> 有线 -> 无线。多网卡时给的是手机最可能连上的那个。</summary>
    Public Shared Function GetLocalIPv4() As String
        Try
            Dim list As List(Of NetworkInterface) = GetActiveAdapters()

            Dim wired As NetworkInterface = Nothing
            For Each ni As NetworkInterface In list
                If ni.NetworkInterfaceType = NetworkInterfaceType.Ethernet OrElse
                   ni.NetworkInterfaceType = NetworkInterfaceType.GigabitEthernet Then
                    wired = ni
                    Exit For
                End If
            Next

            Dim pick As NetworkInterface = If(wired, If(list.Count > 0, list(0), Nothing))
            If pick IsNot Nothing Then
                Dim a As IPAddress = FirstIPv4(pick)
                If a IsNot Nothing Then Return a.ToString()
            End If
        Catch
        End Try
        Return "0.0.0.0"
    End Function

    ''' <summary>每个活动网卡按掩码算出的定向广播地址（多网卡必须逐个发，只发 255.255.255.255 只会走默认路由那块网卡）。</summary>
    Public Shared Function GetBroadcastAddresses() As List(Of IPAddress)
        Dim list As New List(Of IPAddress)()
        Try
            For Each ni As NetworkInterface In GetActiveAdapters()
                For Each ua As UnicastIPAddressInformation In ni.GetIPProperties().UnicastAddresses
                    If Not IsGoodV4(ua.Address) OrElse ua.IPv4Mask Is Nothing Then Continue For
                    Dim ip() As Byte = ua.Address.GetAddressBytes()
                    Dim mk() As Byte = ua.IPv4Mask.GetAddressBytes()
                    If ip.Length <> 4 OrElse mk.Length <> 4 Then Continue For
                    Dim bc(3) As Byte
                    For i As Integer = 0 To 3
                        bc(i) = CByte(ip(i) Or (mk(i) Xor &HFF))
                    Next
                    Dim addr As New IPAddress(bc)
                    If Not list.Contains(addr) Then list.Add(addr)
                Next
            Next
        Catch
        End Try
        Return list
    End Function
End Class

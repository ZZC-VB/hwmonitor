Imports System.Threading
Imports System.Windows.Forms

''' <summary>入口：单实例 + 托盘。端口常量与手机端、小程序端约定一致，不要改回微信黑名单端口。</summary>
Friend Module Program
    Friend Const Port As Integer = 9455          ' TCP 推流
    Friend Const DiscoveryPort As Integer = 9456 ' UDP 发现
    Friend Const DefaultIntervalMs As Integer = 1000
    Friend Const MinIntervalMs As Integer = 1000
    Friend Const MaxIntervalMs As Integer = 60000

    ' 采样线程读、每个连接的接收线程写。用 Volatile 保证采样线程每次都重新读内存，
    ' 不会被 JIT 缓存在寄存器里导致「改了设置却一直按旧间隔跑」。
    Private _intervalMs As Integer = DefaultIntervalMs

    ''' <summary>
    ''' 采样周期，由客户端通过 {"c":"interval","v":秒} 远程设置，范围 1~60 秒。
    ''' 不在磁盘上保存：PC 端保持无配置，手机连上后会立刻把它保存的值下发过来。
    ''' </summary>
    Friend Property IntervalMs As Integer
        Get
            Return Volatile.Read(_intervalMs)
        End Get
        Set(value As Integer)
            If value < MinIntervalMs Then value = MinIntervalMs
            If value > MaxIntervalMs Then value = MaxIntervalMs
            Volatile.Write(_intervalMs, value)
        End Set
    End Property

    <STAThread>
    Friend Sub Main()
        Dim created As Boolean = False
        Using mtx As New Mutex(True, "HwMonitor.SingleInstance", created)
            If Not created Then Return
            Application.EnableVisualStyles()
            Application.Run(New TrayApp())
        End Using
    End Sub
End Module

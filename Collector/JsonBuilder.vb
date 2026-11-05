Imports System.Globalization
Imports System.Text

''' <summary>手写 JSON：零依赖。NaN / Infinity / Nothing 一律输出 null，数值保留 1 位小数。</summary>
Friend NotInheritable Class JsonBuilder
    Private ReadOnly _sb As New StringBuilder(512)
    Private ReadOnly _hasItems As New Stack(Of Boolean)

    Public Sub New()
        _hasItems.Push(False)
    End Sub

    Private Sub Prefix(key As String)
        If _hasItems.Pop() Then _sb.Append(","c)
        _hasItems.Push(True)
        If key IsNot Nothing Then
            _sb.Append(""""c).Append(key).Append(""":")
        End If
    End Sub

    Public Sub OpenObject(Optional key As String = Nothing)
        Prefix(key)
        _sb.Append("{"c)
        _hasItems.Push(False)
    End Sub

    Public Sub CloseObject()
        _hasItems.Pop()
        _sb.Append("}"c)
    End Sub

    Public Sub OpenArray(key As String)
        Prefix(key)
        _sb.Append("["c)
        _hasItems.Push(False)
    End Sub

    Public Sub CloseArray()
        _hasItems.Pop()
        _sb.Append("]"c)
    End Sub

    Public Sub Num(key As String, v As Double?)
        Prefix(key)
        AppendNum(v)
    End Sub

    Public Sub Lng(key As String, v As Long)
        Prefix(key)
        _sb.Append(v.ToString(CultureInfo.InvariantCulture))
    End Sub

    Public Sub Str(key As String, v As String)
        Prefix(key)
        If v Is Nothing Then
            _sb.Append("null")
            Return
        End If
        _sb.Append(""""c)
        For Each ch As Char In v
            Select Case ch
                Case """"c : _sb.Append("\""")
                Case "\"c : _sb.Append("\\")
                Case ControlChars.Lf : _sb.Append("\n")
                Case ControlChars.Cr : _sb.Append("\r")
                Case ControlChars.Tab : _sb.Append("\t")
                Case Else
                    If AscW(ch) < &H20 Then
                        _sb.Append("\u").Append(AscW(ch).ToString("x4"))
                    Else
                        _sb.Append(ch)
                    End If
            End Select
        Next
        _sb.Append(""""c)
    End Sub

    Private Sub AppendNum(v As Double?)
        If Not v.HasValue OrElse Double.IsNaN(v.Value) OrElse Double.IsInfinity(v.Value) Then
            _sb.Append("null")
        Else
            _sb.Append(Math.Round(v.Value, 1).ToString("0.#", CultureInfo.InvariantCulture))
        End If
    End Sub

    Public Overrides Function ToString() As String
        Return _sb.ToString()
    End Function
End Class

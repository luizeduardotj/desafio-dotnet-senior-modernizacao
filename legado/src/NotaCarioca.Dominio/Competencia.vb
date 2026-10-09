Imports System
Imports System.Globalization

Public Class Competencia

    ' aceita "AAAA-MM", "AAAA-MM-DD" ou "AAAA-MM-DDTHH:mm:ss"
    Public Shared Function Parse(ByVal texto As String) As DateTime
        If texto Is Nothing Then Throw New ArgumentNullException("texto")
        Dim t As String = texto.Trim()
        Dim d As DateTime
        If t.Length = 7 Then
            If DateTime.TryParseExact(t, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, d) Then
                Return d
            End If
        Else
            If DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, d) Then
                Return PrimeiroDia(d)
            End If
        End If
        Throw New FormatException("Competencia invalida: " & texto)
    End Function

    Public Shared Function PrimeiroDia(ByVal data As DateTime) As DateTime
        Return New DateTime(data.Year, data.Month, 1)
    End Function

    ' vencimento da guia: dia 10 do mes seguinte
    Public Shared Function Vencimento(ByVal competencia As DateTime) As DateTime
        Return PrimeiroDia(competencia).AddMonths(1).AddDays(9)
    End Function

    ' ultimo instante em que o contribuinte pode cancelar (dia 10 do mes seguinte, 23:59:59)
    Public Shared Function PrazoCancelamento(ByVal competencia As DateTime) As DateTime
        Return Vencimento(competencia).AddDays(1).AddSeconds(-1)
    End Function

    Public Shared Function PrazoCancelamentoExpirado(ByVal competencia As DateTime, ByVal agora As DateTime) As Boolean
        Return agora > PrazoCancelamento(competencia)
    End Function

    Public Shared Function Formatar(ByVal competencia As DateTime) As String
        Return competencia.ToString("yyyy-MM", CultureInfo.InvariantCulture)
    End Function

End Class

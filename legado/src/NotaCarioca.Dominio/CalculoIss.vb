Imports System

' Calculo do ISS das NFS-e - usado pelo processamento de lote de RPS
' alterado em 2017 - chamado 4471 (regime MEI)
Public Class CalculoIss

    Public Shared Function CalcularBase(ByVal valorServicos As Decimal, ByVal valorDeducoes As Decimal) As Decimal
        Return valorServicos - valorDeducoes
    End Function

    Public Shared Function CalcularValorIss(ByVal baseCalculo As Decimal, ByVal aliquota As Decimal, ByVal regime As String) As Decimal
        If regime = "M" Then
            Return 0D
        End If

        Dim valor As Double = CDbl(baseCalculo) * CDbl(aliquota)
        Return CDec(Math.Round(valor, 2))
    End Function

    Public Shared Function AliquotaEfetiva(ByVal aliquotaItem As Decimal, ByVal regime As String) As Decimal
        If regime = "M" Then
            Return 0D
        End If
        Return aliquotaItem
    End Function

End Class

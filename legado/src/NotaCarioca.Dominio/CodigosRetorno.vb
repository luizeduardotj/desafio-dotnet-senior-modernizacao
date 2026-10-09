Imports System
Imports System.Collections.Generic

Public Class CodigosRetorno

    Public Const E001 As String = "E001"
    Public Const E010 As String = "E010"
    Public Const E020 As String = "E020"
    Public Const E030 As String = "E030"
    Public Const E040 As String = "E040"
    Public Const E050 As String = "E050"
    Public Const E051 As String = "E051"
    Public Const E052 As String = "E052"
    Public Const E053 As String = "E053"
    Public Const E060 As String = "E060"
    Public Const E061 As String = "E061"
    Public Const E062 As String = "E062"
    Public Const E070 As String = "E070"
    Public Const E071 As String = "E071"
    Public Const E999 As String = "E999"

    Private Shared ReadOnly _mensagens As Dictionary(Of String, String()) = Montar()

    Private Shared Function Montar() As Dictionary(Of String, String())
        Dim d As New Dictionary(Of String, String())
        d.Add(E001, New String() {"XML mal formado ou inválido.", "Verifique a estrutura do XML enviado."})
        d.Add(E010, New String() {"Prestador não encontrado ou inativo.", "Verifique a Inscrição Municipal informada."})
        d.Add(E020, New String() {"CPF/CNPJ inválido.", "Informe um CPF ou CNPJ válido."})
        d.Add(E030, New String() {"Período inválido.", "A data inicial deve ser menor ou igual à data final e o período não pode exceder 31 dias."})
        d.Add(E040, New String() {"Nenhuma NFS-e encontrada.", "Altere os critérios da consulta."})
        d.Add(E050, New String() {"NFS-e não encontrada.", "Verifique o número da NFS-e e o prestador."})
        d.Add(E051, New String() {"NFS-e substituída não pode ser cancelada.", "Cancele a NFS-e substituidora, se necessário."})
        d.Add(E052, New String() {"Prazo de cancelamento expirado.", "O cancelamento é permitido até o dia 10 do mês seguinte à competência."})
        d.Add(E053, New String() {"Guia da competência já paga.", "Procure a Prefeitura para solicitar o cancelamento administrativo."})
        d.Add(E060, New String() {"RPS já informado.", "Um RPS só pode originar uma NFS-e."})
        d.Add(E061, New String() {"Item de serviço inexistente.", "Informe um item da lista de serviços (LC 116/2003)."})
        d.Add(E062, New String() {"Valor de serviços inválido.", "O valor de serviços deve ser maior que zero e maior ou igual às deduções."})
        d.Add(E070, New String() {"Lote não encontrado.", "Verifique o protocolo informado."})
        d.Add(E071, New String() {"Lote ainda não processado.", "Aguarde e consulte novamente."})
        d.Add(E999, New String() {"Erro interno no processamento.", "Tente novamente mais tarde."})
        Return d
    End Function

    Public Shared Function Mensagem(ByVal codigo As String) As String
        If _mensagens.ContainsKey(codigo) Then Return _mensagens(codigo)(0)
        Return "Erro não catalogado."
    End Function

    Public Shared Function Correcao(ByVal codigo As String) As String
        If _mensagens.ContainsKey(codigo) Then Return _mensagens(codigo)(1)
        Return ""
    End Function

End Class

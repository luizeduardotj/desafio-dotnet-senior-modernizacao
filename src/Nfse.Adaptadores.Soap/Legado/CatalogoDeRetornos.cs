namespace Nfse.Adaptadores.Soap.Legado;

/// <summary>Códigos, mensagens e correções exatamente como em <c>legado/src/NotaCarioca.Dominio/CodigosRetorno.vb</c>.</summary>
internal static class CatalogoDeRetornos
{
    public const string E001 = "E001";
    public const string E010 = "E010";
    public const string E020 = "E020";
    public const string E030 = "E030";
    public const string E040 = "E040";
    public const string E050 = "E050";
    public const string E051 = "E051";
    public const string E052 = "E052";
    public const string E053 = "E053";

    private static readonly Dictionary<string, (string Mensagem, string Correcao)> Textos = new(StringComparer.Ordinal)
    {
        [E001] = ("XML mal formado ou inválido.", "Verifique a estrutura do XML enviado."),
        [E010] = ("Prestador não encontrado ou inativo.", "Verifique a Inscrição Municipal informada."),
        [E020] = ("CPF/CNPJ inválido.", "Informe um CPF ou CNPJ válido."),
        [E030] = ("Período inválido.", "A data inicial deve ser menor ou igual à data final e o período não pode exceder 31 dias."),
        [E040] = ("Nenhuma NFS-e encontrada.", "Altere os critérios da consulta."),
        [E050] = ("NFS-e não encontrada.", "Verifique o número da NFS-e e o prestador."),
        [E051] = ("NFS-e substituída não pode ser cancelada.", "Cancele a NFS-e substituidora, se necessário."),
        [E052] = ("Prazo de cancelamento expirado.", "O cancelamento é permitido até o dia 10 do mês seguinte à competência."),
        [E053] = ("Guia da competência já paga.", "Procure a Prefeitura para solicitar o cancelamento administrativo."),
    };

    public static string Mensagem(string codigo) => Textos.TryGetValue(codigo, out var t) ? t.Mensagem : "Erro não catalogado.";

    public static string Correcao(string codigo) => Textos.TryGetValue(codigo, out var t) ? t.Correcao : "";
}

namespace Nfse.Fachada.Sombra;

/// <summary>Classes automáticas da sombra de leitura (ADR-0009, seção 4). <c>bug_novo</c>/<c>bug_legado</c> são da triagem humana.</summary>
internal enum ClasseDeDivergencia
{
    NaoClassificada,
    Corrida,
    ErroNovo,
    DecisaoConsciente,
}

/// <summary>
/// Regras de classificação que não precisam de reexecução. Só olham protocolo (envelope, Fault, DTD), nunca regra de
/// NFS-e: as classes de negócio do cancelamento são do serviço novo (ADR-0008).
/// </summary>
internal static class ClassificadorDeDivergencia
{
    public static string Nome(ClasseDeDivergencia classe) => classe switch
    {
        ClasseDeDivergencia.Corrida => "corrida",
        ClasseDeDivergencia.ErroNovo => "erro_novo",
        ClasseDeDivergencia.DecisaoConsciente => "decisao_consciente",
        _ => "nao_classificada",
    };

    /// <summary>
    /// Classe de uma divergência já confirmada em bytes, antes de reexecutar. <c>null</c>: nenhuma regra casa e a
    /// reexecução decide entre <c>corrida</c> e <c>nao_classificada</c>.
    /// </summary>
    /// <returns>A classe e, em <c>decisao_consciente</c>, o ID do catálogo da ADR-0004.</returns>
    public static (ClasseDeDivergencia Classe, string? Regra)? SemReexecucao(
        ReadOnlySpan<byte> requisicao, int statusLegado, ReadOnlySpan<byte> legado, int statusNovo, ReadOnlySpan<byte> novo)
    {
        if (C20_FaultServerComTextoDiferente(statusLegado, legado, statusNovo, novo))
        {
            return (ClasseDeDivergencia.DecisaoConsciente, "C20");
        }

        if (statusNovo >= 500)
        {
            return (ClasseDeDivergencia.ErroNovo, null);
        }

        if (C28_PedidoComDtd(requisicao))
        {
            return (ClasseDeDivergencia.DecisaoConsciente, "C28");
        }

        return null;
    }

    /// <summary>
    /// C20: o legado devolve <c>ex.Message</c> (inclusive do Oracle) no <c>soap:Fault</c>; o novo devolve o mesmo Fault
    /// <c>soap:Server</c> com texto genérico. Os dois são Fault de servidor, só o texto difere.
    /// </summary>
    internal static bool C20_FaultServerComTextoDiferente(int statusLegado, ReadOnlySpan<byte> legado, int statusNovo, ReadOnlySpan<byte> novo) =>
        statusLegado == statusNovo
        && XmlSoap.Fault(legado) is { Codigo: "soap:Server" } faultLegado
        && XmlSoap.Fault(novo) is { Codigo: "soap:Server" } faultNovo
        && !string.Equals(faultLegado.Texto, faultNovo.Texto, StringComparison.Ordinal);

    /// <summary>
    /// C28: o legado processa DTD interna e expande entidades; o novo rejeita DTD. Basta a declaração no envelope ou no
    /// <c>inputXML</c> (escapada) para a diferença ser esperada.
    /// </summary>
    internal static bool C28_PedidoComDtd(ReadOnlySpan<byte> requisicao) =>
        requisicao.IndexOf("<!DOCTYPE"u8) >= 0 || requisicao.IndexOf("&lt;!DOCTYPE"u8) >= 0;
}

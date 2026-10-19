using System.Xml;
using Nfse.Adaptadores.Soap.Legado;
using Nfse.Aplicacao.Consultas;

namespace Nfse.Adaptadores.Soap.Operacoes;

/// <summary>
/// <c>ConsultarNfse</c> no contrato do legado (<c>ConsultaNfseServico.ConsultarNfse</c>), com a ordem de validação
/// E001 → E010 → E020 → E030 → consulta → E040 (C24).
/// </summary>
internal sealed class ConsultarNfseSoap(ConsultaDeNotasEmitidas consulta)
{
    private const string Raiz = "ConsultarNfseResposta";

    public async Task<string> ExecutarAsync(string entrada, CancellationToken cancellationToken)
    {
        XmlDocument documento;
        try
        {
            documento = CompatibilidadeLegado.Carregar(entrada);
        }
        catch (XmlException)
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        XmlElement? raiz = documento.DocumentElement;
        if (raiz is null || raiz.Name != "ConsultarNfseEnvio")
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        // C18: o prestador vem só da inscrição municipal; o CNPJ do pedido é ignorado.
        string? inscricao = CompatibilidadeLegado.Texto(raiz, "Prestador/InscricaoMunicipal");
        PrestadorLido? prestador = string.IsNullOrEmpty(inscricao)
            ? null
            : await consulta.PrestadorAtivoAsync(inscricao, cancellationToken).ConfigureAwait(false);
        if (prestador is null)
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E010);
        }

        string? tomador = CompatibilidadeLegado.LerDocumento(raiz.SelectSingleNode("Tomador"));
        if (!string.IsNullOrEmpty(tomador) && !CompatibilidadeLegado.ValidarCpfOuCnpj(tomador))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E020);
        }

        if (!CompatibilidadeLegado.LerPeriodo(raiz, out DateTime inicio, out DateTime fim))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E030);
        }

        // C01/C02: DataFinal exclusiva. C13: competência até a DataFinal.
        var filtro = new FiltroDeNotasEmitidas(
            prestador.Id,
            inicio,
            fim,
            TomadorDigitos: string.IsNullOrEmpty(tomador) ? null : CompatibilidadeLegado.SomenteDigitos(tomador),
            CompetenciaAte: fim);

        (int deslocamento, int limite) = CompatibilidadeLegado.Faixa(CompatibilidadeLegado.LerPagina(raiz));
        IReadOnlyList<NotaFiscalLida> notas = await consulta
            .ListarPorFaixaAsync(filtro, deslocamento, limite, cancellationToken).ConfigureAwait(false);

        // C06: página além do fim responde o mesmo E040 de consulta sem resultado.
        return notas.Count == 0
            ? SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E040)
            : SerializadorLegado.Pagina(Raiz, notas, CompatibilidadeLegado.TamanhoDaPagina);
    }
}

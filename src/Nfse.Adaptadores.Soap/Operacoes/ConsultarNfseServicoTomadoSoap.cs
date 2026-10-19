using System.Xml;
using Nfse.Adaptadores.Soap.Legado;
using Nfse.Aplicacao.Consultas;

namespace Nfse.Adaptadores.Soap.Operacoes;

/// <summary>
/// <c>ConsultarNfseServicoTomado</c> no contrato do legado (<c>ConsultaNfseServico.ConsultarNfseServicoTomado</c>):
/// E001 → E020 → E030 → consulta → E040, sem E010 (C24).
/// </summary>
internal sealed class ConsultarNfseServicoTomadoSoap(ConsultaDeNotasTomadas consulta)
{
    private const string Raiz = "ConsultarNfseServicoTomadoResposta";

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
        if (raiz is null || raiz.Name != "ConsultarNfseServicoTomadoEnvio")
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E001);
        }

        // C26: Consulente e, se vazio, Tomador. C19: nenhuma autorização sobre quem consulta (risco aceito).
        string? tomador = CompatibilidadeLegado.LerDocumento(raiz.SelectSingleNode("Consulente"));
        if (string.IsNullOrEmpty(tomador))
        {
            tomador = CompatibilidadeLegado.LerDocumento(raiz.SelectSingleNode("Tomador"));
        }

        if (!CompatibilidadeLegado.ValidarCpfOuCnpj(tomador))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E020);
        }

        if (!CompatibilidadeLegado.LerPeriodo(raiz, out DateTime inicio, out DateTime fim))
        {
            return SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E030);
        }

        // C01/C02: DataFinal exclusiva. C15 corrigido: documento comparado como texto.
        var filtro = new FiltroDeNotasTomadas(CompatibilidadeLegado.SomenteDigitos(tomador), inicio, fim);

        (int deslocamento, int limite) = CompatibilidadeLegado.Faixa(CompatibilidadeLegado.LerPagina(raiz));
        IReadOnlyList<NotaFiscalLida> notas = await consulta
            .ListarPorFaixaAsync(filtro, deslocamento, limite, cancellationToken).ConfigureAwait(false);

        return notas.Count == 0
            ? SerializadorLegado.RespostaErro(Raiz, CatalogoDeRetornos.E040)
            : SerializadorLegado.Pagina(Raiz, notas, CompatibilidadeLegado.TamanhoDaPagina);
    }
}

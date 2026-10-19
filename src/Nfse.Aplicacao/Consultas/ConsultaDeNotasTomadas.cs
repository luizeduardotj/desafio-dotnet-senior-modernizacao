using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Comum;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Consultas;

/// <summary>
/// Caso de uso de leitura: notas recebidas por um tomador, de todos os prestadores. Quem pode consultar qual documento
/// é decidido pelo adaptador de entrada (na REST, o titular do token; no SOAP, ninguém: C19, risco aceito).
/// </summary>
public sealed class ConsultaDeNotasTomadas(ILeituraDeNotas leitura)
{
    /// <summary>Faixa por posição, para o contrato SOAP. A validação do pedido é de quem chama.</summary>
    public Task<IReadOnlyList<NotaFiscalLida>> ListarPorFaixaAsync(
        FiltroDeNotasTomadas filtro, int deslocamento, int limite, CancellationToken cancellationToken) =>
        leitura.ListarTomadasPorFaixaAsync(filtro, deslocamento, limite, cancellationToken);

    /// <summary>Página por cursor no período inclusivo (contrato REST).</summary>
    public async Task<Resultado<PaginaDeNotas>> ListarAsync(
        Documento tomador,
        PeriodoDeEmissao periodo,
        SituacaoNota? situacao,
        PosicaoNaListagem? apos,
        int limite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tomador);
        ArgumentNullException.ThrowIfNull(periodo);

        if (limite is < 1 or > ConsultaDeNotasEmitidas.LimiteMaximo)
        {
            return new LimiteInvalido(ConsultaDeNotasEmitidas.LimiteMaximo);
        }

        var filtro = new FiltroDeNotasTomadas(
            tomador.Digitos,
            periodo.Inicio.ToDateTime(TimeOnly.MinValue),
            periodo.Fim.AddDays(1).ToDateTime(TimeOnly.MinValue),
            situacao);

        IReadOnlyList<NotaFiscalLida> notas = await leitura
            .ListarTomadasAposAsync(filtro, apos, limite + 1, cancellationToken).ConfigureAwait(false);

        return Paginas.Cortar(notas, limite);
    }
}

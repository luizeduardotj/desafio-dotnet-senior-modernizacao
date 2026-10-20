using System.Diagnostics.Metrics;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Comum;

namespace Nfse.Aplicacao.Cancelamento;

/// <summary>
/// Command de cancelamento (ADR-0003). O PL/SQL decide; a <see cref="PoliticaDeCancelamento"/> roda sobre o mesmo
/// snapshot lido sob lock (dual-run) e a divergência vira métrica, sem mudar o resultado.
/// </summary>
public sealed class CancelamentoDeNota(ConsultaDeNotasEmitidas consulta, IGatewayDeCancelamento gateway)
{
    public const string NomeDoMedidor = "Nfse.Host";

    private static readonly Meter Medidor = new(NomeDoMedidor);

    private static readonly Counter<long> Divergencias = Medidor.CreateCounter<long>(
        "cancelamento_regra_divergente_total",
        description: "Cancelamentos em que a regra do domínio discordou do PL/SQL (dual-run, ADR-0003).");

    public async Task<Resultado<RespostaDoCancelamento>> CancelarAsync(
        string inscricaoMunicipal, long numero, string motivo, CancellationToken cancellationToken)
    {
        PrestadorLido? prestador = await consulta.PrestadorAtivoAsync(inscricaoMunicipal, cancellationToken).ConfigureAwait(false);
        if (prestador is null)
        {
            return new PrestadorNaoEncontrado();
        }

        ResultadoDoGateway resultado = await gateway.CancelarAsync(prestador.Id, numero, motivo, cancellationToken).ConfigureAwait(false);
        bool divergente = CompararComODominio(resultado);
        return new RespostaDoCancelamento(resultado.Decisao, resultado.DataCancelamento, divergente);
    }

    /// <returns>Se a divergência ficou <c>nao_classificada</c> (a de fronteira de relógio é explicada).</returns>
    private static bool CompararComODominio(ResultadoDoGateway resultado)
    {
        // Nota inexistente não tem snapshot: as duas regras concordam por construção.
        if (resultado.EstadoAntes is null || resultado.AgoraNoBanco is not { } agora)
        {
            return false;
        }

        DecisaoDeCancelamento dominio = PoliticaDeCancelamento.Avaliar(resultado.EstadoAntes, agora);
        if (dominio == resultado.Decisao)
        {
            return false;
        }

        // O SYSDATE do PL/SQL pode cair no segundo seguinte ao lido pelo wrapper.
        bool fronteira = Math.Abs((resultado.EstadoAntes.Competencia.FimDoPrazoDeCancelamento - agora).TotalSeconds) <= 2;
        Divergencias.Add(1,
            new KeyValuePair<string, object?>("decisao_plsql", resultado.Decisao.ToString()),
            new KeyValuePair<string, object?>("decisao_dominio", dominio.ToString()),
            new KeyValuePair<string, object?>("classe", fronteira ? "fronteira_relogio" : "nao_classificada"));
        return !fronteira;
    }
}

using Dapper;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Notas;

namespace Nfse.Infra.Oracle;

/// <summary>
/// Estado atual para a sombra do cancelamento, pelo pool do <c>NFSE_MODERNO_RO</c> (só <c>READ</c> nas views, sem
/// <c>EXECUTE</c> no wrapper). O <c>SYSDATE</c> vem na mesma consulta.
/// </summary>
public sealed class LeituraParaSombra(FabricaDeConexoes conexoesSomenteLeitura) : ILeituraParaSombra
{
    private const string Sql =
        "SELECT SYSDATE AS AGORA, n.SITUACAO, n.COMPETENCIA, n.DT_CANCELAMENTO, " +
        "(SELECT COUNT(*) FROM NFSE_MODERNO.V_GUIA g WHERE g.PRESTADOR_ID = :prestador AND g.COMPETENCIA = n.COMPETENCIA AND g.SITUACAO = 'P') AS GUIA_PAGA, " +
        "(SELECT MAX(g.DT_ATUALIZACAO) FROM NFSE_MODERNO.V_GUIA g WHERE g.PRESTADOR_ID = :prestador AND g.COMPETENCIA = n.COMPETENCIA) AS GUIA_ATUALIZADA " +
        "FROM DUAL LEFT JOIN NFSE_MODERNO.V_NFSE n ON n.PRESTADOR_ID = :prestador AND n.NUMERO = :numero";

    public async Task<EstadoAtualDaNota> LerAsync(long prestadorId, long numero, CancellationToken cancellationToken)
    {
        await using var conexao = await conexoesSomenteLeitura.AbrirAsync(cancellationToken).ConfigureAwait(false);
        Linha linha = await conexao.QuerySingleAsync<Linha>(
            new CommandDefinition(Sql, new { prestador = prestadorId, numero }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new EstadoAtualDaNota(
            linha.AGORA,
            linha.SITUACAO switch
            {
                null => null,
                "C" => SituacaoNota.Cancelada,
                "S" => SituacaoNota.Substituida,
                _ => SituacaoNota.Normal,
            },
            linha.COMPETENCIA is { } c ? Competencia.De(DateOnly.FromDateTime(c)) : null,
            linha.DT_CANCELAMENTO,
            linha.GUIA_PAGA > 0,
            linha.GUIA_ATUALIZADA);
    }

#pragma warning disable CA1812, CA1707 // instanciada pelo Dapper; nomes iguais às colunas
    private sealed class Linha
    {
        public DateTime AGORA { get; set; }
        public string? SITUACAO { get; set; }
        public DateTime? COMPETENCIA { get; set; }
        public DateTime? DT_CANCELAMENTO { get; set; }
        public decimal GUIA_PAGA { get; set; }
        public DateTime? GUIA_ATUALIZADA { get; set; }
    }
#pragma warning restore CA1812, CA1707
}

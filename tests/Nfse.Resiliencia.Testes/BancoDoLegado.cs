using Oracle.ManagedDataAccess.Client;

namespace Nfse.Resiliencia.Testes;

/// <summary>Acesso direto ao Oracle como NFSE_OWNER, fora do Toxiproxy: outra sessão do legado e o estado real da nota.</summary>
public static class BancoDoLegado
{
    public sealed record EstadoDaNota(string Situacao, DateTime? DataCancelamento, string? Motivo);

    public static async Task<EstadoDaNota> NotaAsync(string conexao, int prestador, long numero)
    {
        await using var c = new OracleConnection(conexao);
        await c.OpenAsync();
        await using var comando = new OracleCommand(
            "SELECT SITUACAO, DT_CANCELAMENTO, MOTIVO_CANCELAMENTO FROM NFSE WHERE PRESTADOR_ID = :p AND NUMERO = :n", c)
        {
            BindByName = true,
        };
        comando.Parameters.Add("p", prestador);
        comando.Parameters.Add("n", numero);
        await using var leitor = await comando.ExecuteReaderAsync();
        if (!await leitor.ReadAsync())
        {
            throw new InvalidOperationException($"nota {prestador}/{numero} não existe no seed");
        }

        return new EstadoDaNota(leitor.GetString(0), leitor.IsDBNull(1) ? null : leitor.GetDateTime(1), leitor.IsDBNull(2) ? null : leitor.GetString(2));
    }

    /// <summary>
    /// Uma nota ainda normal do prestador 25 entre 3 e 8: a 1 e a 2 são do corpus da paridade (casos 210 a 212). Cada
    /// execução cancela duas; com o banco recém-carregado, dá para repetir três vezes.
    /// </summary>
    public static async Task<long> NotaParaCancelarAsync(string conexao)
    {
        await using var c = new OracleConnection(conexao);
        await c.OpenAsync();
        await using var comando = new OracleCommand(
            "SELECT MIN(NUMERO) FROM NFSE WHERE PRESTADOR_ID = 25 AND NUMERO BETWEEN 3 AND 8 AND SITUACAO = 'N'", c);
        object? numero = await comando.ExecuteScalarAsync();
        return numero is decimal n
            ? (long)n
            : throw new InvalidOperationException("sem nota normal do prestador 25 para cancelar: suba o compose de resiliência do zero (down -v)");
    }

    /// <summary>
    /// Segura o lock da linha da nota numa transação aberta, como um cancelamento do legado em curso. O
    /// <c>DisposeAsync</c> faz rollback e solta o lock.
    /// </summary>
    public static async Task<Lock> TravarNotaAsync(string conexao, int prestador, long numero)
    {
        var c = new OracleConnection(conexao);
        await c.OpenAsync();
        OracleTransaction transacao = c.BeginTransaction();
        await using var comando = new OracleCommand(
            "SELECT ID FROM NFSE WHERE PRESTADOR_ID = :p AND NUMERO = :n FOR UPDATE", c)
        {
            BindByName = true,
            Transaction = transacao,
        };
        comando.Parameters.Add("p", prestador);
        comando.Parameters.Add("n", numero);
        await comando.ExecuteScalarAsync();
        return new Lock(c, transacao);
    }

    public sealed class Lock(OracleConnection conexao, OracleTransaction transacao) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transacao.RollbackAsync();
            await transacao.DisposeAsync();
            await conexao.DisposeAsync();
        }
    }
}

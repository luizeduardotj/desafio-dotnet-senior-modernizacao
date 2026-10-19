using System.Data;
using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;

namespace Nfse.Paridade.Testes;

/// <summary>
/// Garantias do Oracle que o serviço novo pressupõe (ADR-0002, ADR-0003, ADR-0009), antes só conferidas à mão no
/// spike de 2026-10-19. Rodam contra o banco do compose pelos usuários de runtime, como a paridade.
/// </summary>
/// <remarks>
/// Os códigos de erro dependem da versão: o compose é 23ai e produção é 19c. Cada teste aceita a classe do erro nas
/// duas versões (sem privilégio, objeto invisível), não um código fixo.
/// </remarks>
public sealed class GarantiasDeGrantsTests
{
    // Objeto sem nenhum privilégio: ORA-00942 nas duas versões. O 23ai pode detalhar como ORA-41900 (falta READ/SELECT).
    private static readonly int[] ObjetoInvisivel = [942, 41900];

    // Objeto visível só com READ, pedindo FOR UPDATE ou DML: ORA-01031 no 19c, ORA-41900 no 23ai (visto no spike).
    private static readonly int[] SemPrivilegio = [1031, 41900];

    private const string ChamadaDoWrapper =
        "DECLARE e VARCHAR2(10); s VARCHAR2(1); c DATE; g NUMBER; a DATE; d DATE; " +
        "BEGIN NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar(0, 0, 'garantia', e, s, c, g, a, d); END;";

    [GarantiaOracleFact(Usuario.SomenteLeitura)]
    public async Task Somente_leitura_le_as_tres_views()
    {
        await using OracleConnection conexao = await Banco.AbrirAsync(Usuario.SomenteLeitura);
        foreach (string view in new[] { "V_NFSE", "V_CONTRIBUINTE", "V_GUIA" })
        {
            await using var comando = new OracleCommand($"SELECT COUNT(*) FROM NFSE_MODERNO.{view} WHERE ROWNUM = 1", conexao);
            Assert.Equal(1m, (decimal)(await comando.ExecuteScalarAsync())!);
        }
    }

    [GarantiaOracleFact(Usuario.SomenteLeitura)]
    public async Task Somente_leitura_nao_le_a_tabela_do_legado()
    {
        OracleException erro = await Banco.FalhaAsync(Usuario.SomenteLeitura, "SELECT COUNT(*) FROM NFSE_OWNER.NFSE WHERE ROWNUM = 1");
        Assert.Contains(erro.Number, ObjetoInvisivel);
    }

    [GarantiaOracleFact(Usuario.SomenteLeitura)]
    public async Task Somente_leitura_nao_executa_o_wrapper()
    {
        // A sombra usa este usuário (ADR-0009): o wrapper nem compila na chamada. ORA-06550 com PLS-00201 nas duas versões.
        OracleException erro = await Banco.FalhaAsync(Usuario.SomenteLeitura, ChamadaDoWrapper);
        Assert.Equal(6550, erro.Number);
        Assert.Contains("PLS-00201", erro.Message, StringComparison.Ordinal);
    }

    [GarantiaOracleFact(Usuario.Aplicacao)]
    public async Task Aplicacao_nao_le_nem_altera_a_tabela_do_legado()
    {
        OracleException leitura = await Banco.FalhaAsync(Usuario.Aplicacao, "SELECT COUNT(*) FROM NFSE_OWNER.NFSE WHERE ROWNUM = 1");
        Assert.Contains(leitura.Number, ObjetoInvisivel);

        OracleException escrita = await Banco.FalhaAsync(Usuario.Aplicacao, "UPDATE NFSE_OWNER.NFSE SET MOTIVO_CANCELAMENTO = MOTIVO_CANCELAMENTO WHERE 1 = 0");
        Assert.Contains(escrita.Number, ObjetoInvisivel);
    }

    [GarantiaOracleFact(Usuario.Aplicacao)]
    public async Task Aplicacao_nao_executa_o_pacote_do_legado_direto()
    {
        OracleException erro = await Banco.FalhaAsync(Usuario.Aplicacao,
            "DECLARE e VARCHAR2(10); BEGIN NFSE_OWNER.PKG_NFSE.cancelar_nfse(0, 0, 'garantia', e); END;");
        Assert.Equal(6550, erro.Number);
        Assert.Contains("PLS-00201", erro.Message, StringComparison.Ordinal);
    }

    [GarantiaOracleFact(Usuario.Aplicacao)]
    public async Task Aplicacao_so_le_as_views_sem_lock_nem_dml()
    {
        // READ, não SELECT (ADR-0002): a escrita só passa pelo wrapper. WHERE 1 = 0 não toca linha nenhuma se o grant mudar.
        OracleException lockNaView = await Banco.FalhaAsync(Usuario.Aplicacao, "SELECT ID FROM NFSE_MODERNO.V_CONTRIBUINTE WHERE 1 = 0 FOR UPDATE");
        Assert.Contains(lockNaView.Number, SemPrivilegio);

        OracleException dmlNaView = await Banco.FalhaAsync(Usuario.Aplicacao, "UPDATE NFSE_MODERNO.V_CONTRIBUINTE SET RAZAO_SOCIAL = RAZAO_SOCIAL WHERE 1 = 0");
        Assert.Contains(dmlNaView.Number, SemPrivilegio);
    }
}

/// <summary>
/// K08 corrigido pelo wrapper (ADR-0003, fato 3; ADR-0009): dois cancelamentos paralelos da mesma nota pelo
/// <c>PKG_CANCELAMENTO_API</c> fazem um único <c>UPDATE</c>. Escreve no banco: usa a nota 5 do prestador 27, que
/// nenhum caso do corpus nem outro teste toca (o corpus cancela 25/1 e 25/2 e usa 27/2 só no K11, que falha), cujo
/// tomador não aparece nas consultas por tomador e cuja competência é a do mês da carga, com prazo aberto pelo
/// relógio real. Só roda com <c>NFSE_PARIDADE_K08=1</c>, uma vez por banco na linha de base.
/// </summary>
public sealed class GarantiaK08Tests
{
    private const int Prestador = 27;
    private const int Numero = 5;

    // A sessão A segura o lock por este tempo depois de cancelar; se B gravasse de novo, o SYSDATE dela ficaria ao menos
    // isso à frente, e a diferença aparece no DT_CANCELAMENTO e no DT_ATUALIZACAO da guia (precisão de 1 s).
    private static readonly TimeSpan Retencao = TimeSpan.FromSeconds(3);

    [GarantiaK08Fact]
    public async Task K08_cancelamentos_paralelos_pelo_wrapper_gravam_uma_vez()
    {
        (string situacao, DateTime competencia) = await NotaAsync();
        Assert.True(situacao == "N", $"banco fora da linha de base: nota {Prestador}/{Numero} já está '{situacao}'; restaure o banco (tests/paridade/README.md)");
        (decimal issAntes, DateTime atualizacaoAntes) = await GuiaAsync(competencia);

        await using OracleConnection conexaoA = await Banco.AbrirAsync(Usuario.Aplicacao);
        await using OracleTransaction transacaoA = conexaoA.BeginTransaction();
        Chamada a = await CancelarAsync(conexaoA, transacaoA, "K08 sessao A");
        Assert.Null(a.Codigo);
        Assert.Equal("N", a.SituacaoAntes);

        var bChamou = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(Chamada Resultado, TimeSpan Espera)> sessaoB = Task.Run(async () =>
        {
            await using OracleConnection conexaoB = await Banco.AbrirAsync(Usuario.Aplicacao);
            await using OracleTransaction transacaoB = conexaoB.BeginTransaction();
            var relogio = Stopwatch.StartNew();
            Task<Chamada> chamada = CancelarAsync(conexaoB, transacaoB, "K08 sessao B");
            bChamou.SetResult();
            Chamada resultado = await chamada;
            relogio.Stop();
            await transacaoB.CommitAsync();
            return (resultado, relogio.Elapsed);
        });

        await bChamou.Task;
        await Task.Delay(Retencao);
        await transacaoA.CommitAsync();
        (Chamada b, TimeSpan espera) = await sessaoB;

        // B esperou o lock de A (não correu em paralelo), achou a nota já cancelada e saiu pelo caminho idempotente.
        Assert.True(espera >= Retencao - TimeSpan.FromMilliseconds(500), $"a sessão B não esperou o lock: {espera.TotalMilliseconds:F0} ms");
        Assert.Null(b.Codigo);
        Assert.Equal("C", b.SituacaoAntes);
        Assert.Equal(a.DtCancelamento, b.DtCancelamento);

        (string situacaoDepois, _) = await NotaAsync();
        Assert.Equal("C", situacaoDepois);
        (DateTime? dtCancelamento, string? motivo) = await CancelamentoAsync();
        Assert.Equal(a.DtCancelamento, dtCancelamento);
        Assert.Equal("K08 sessao A", motivo);

        // Guia recalculada uma vez, pelo trigger do UPDATE de A. Um segundo UPDATE dispararia o trigger de novo ao menos
        // `Retencao` depois. O valor fica igual ao de antes porque o trigger autônomo não vê a nota cancelada (K07,
        // preservado na migração, ADR-0003 §4); se a ADR-0005 corrigir o trigger, ele passa a ser antes − ISS da nota.
        (decimal issDepois, DateTime atualizacaoDepois) = await GuiaAsync(competencia);
        Assert.True(atualizacaoDepois > atualizacaoAntes, "a guia não foi recalculada");
        Assert.InRange(atualizacaoDepois - a.DtCancelamento!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        Assert.Equal(issAntes, issDepois);
    }

    private static async Task<Chamada> CancelarAsync(OracleConnection conexao, OracleTransaction transacao, string motivo)
    {
        await using var comando = new OracleCommand("NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar", conexao)
        {
            CommandType = CommandType.StoredProcedure,
            BindByName = true,
            CommandTimeout = 15,
            Transaction = transacao,
        };
        comando.Parameters.Add("p_prestador_id", OracleDbType.Int64).Value = Prestador;
        comando.Parameters.Add("p_numero", OracleDbType.Int64).Value = Numero;
        comando.Parameters.Add("p_motivo", OracleDbType.Varchar2).Value = motivo;
        OracleParameter codigo = comando.Parameters.Add("o_codigo_erro", OracleDbType.Varchar2, 10, null, ParameterDirection.Output);
        OracleParameter situacao = comando.Parameters.Add("o_situacao_antes", OracleDbType.Varchar2, 1, null, ParameterDirection.Output);
        comando.Parameters.Add("o_competencia", OracleDbType.Date, ParameterDirection.Output);
        comando.Parameters.Add("o_guia_paga_antes", OracleDbType.Int32, ParameterDirection.Output);
        comando.Parameters.Add("o_agora_banco", OracleDbType.Date, ParameterDirection.Output);
        OracleParameter dtCancelamento = comando.Parameters.Add("o_dt_cancelamento", OracleDbType.Date, ParameterDirection.Output);

        await comando.ExecuteNonQueryAsync();
        return new Chamada(
            codigo.Value is Oracle.ManagedDataAccess.Types.OracleString { IsNull: false } c ? c.Value : null,
            situacao.Value is Oracle.ManagedDataAccess.Types.OracleString { IsNull: false } s ? s.Value : null,
            dtCancelamento.Value is Oracle.ManagedDataAccess.Types.OracleDate { IsNull: false } d ? d.Value : null);
    }

    private static async Task<(string Situacao, DateTime Competencia)> NotaAsync()
    {
        await using OracleConnection conexao = await Banco.AbrirAsync(Usuario.Aplicacao);
        await using var comando = new OracleCommand(
            "SELECT SITUACAO, COMPETENCIA FROM NFSE_MODERNO.V_NFSE WHERE PRESTADOR_ID = :p AND NUMERO = :n", conexao);
        comando.Parameters.Add("p", Prestador);
        comando.Parameters.Add("n", Numero);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync(), $"nota {Prestador}/{Numero} não existe no seed");
        return (leitor.GetString(0), leitor.GetDateTime(1));
    }

    private static async Task<(DateTime? DtCancelamento, string? Motivo)> CancelamentoAsync()
    {
        await using OracleConnection conexao = await Banco.AbrirAsync(Usuario.Aplicacao);
        await using var comando = new OracleCommand(
            "SELECT DT_CANCELAMENTO, MOTIVO_CANCELAMENTO FROM NFSE_MODERNO.V_NFSE WHERE PRESTADOR_ID = :p AND NUMERO = :n", conexao);
        comando.Parameters.Add("p", Prestador);
        comando.Parameters.Add("n", Numero);
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync());
        return (leitor.IsDBNull(0) ? null : leitor.GetDateTime(0), leitor.IsDBNull(1) ? null : leitor.GetString(1));
    }

    private static async Task<(decimal ValorIss, DateTime Atualizacao)> GuiaAsync(DateTime competencia)
    {
        await using OracleConnection conexao = await Banco.AbrirAsync(Usuario.Aplicacao);
        await using var comando = new OracleCommand(
            "SELECT VALOR_ISS, DT_ATUALIZACAO, SITUACAO FROM NFSE_MODERNO.V_GUIA WHERE PRESTADOR_ID = :p AND COMPETENCIA = :c", conexao);
        comando.Parameters.Add("p", Prestador);
        comando.Parameters.Add("c", OracleDbType.Date).Value = competencia;
        await using var leitor = await comando.ExecuteReaderAsync();
        Assert.True(await leitor.ReadAsync(), $"guia do prestador {Prestador} na competência {competencia:yyyy-MM} não existe");
        Assert.Equal("A", leitor.GetString(2));
        return (leitor.GetDecimal(0), leitor.GetDateTime(1));
    }

    private sealed record Chamada(string? Codigo, string? SituacaoAntes, DateTime? DtCancelamento);
}

public enum Usuario
{
    Aplicacao,
    SomenteLeitura,
}

internal static class Banco
{
    public static async Task<OracleConnection> AbrirAsync(Usuario usuario)
    {
        string variavel = usuario == Usuario.Aplicacao ? ParidadeTheoryAttribute.Variavel : SombraOracleTheoryAttribute.Variavel;
        var conexao = new OracleConnection(Environment.GetEnvironmentVariable(variavel));
        await conexao.OpenAsync();
        return conexao;
    }

    /// <summary>Executa o comando numa transação desfeita no fim e devolve o erro do Oracle; falha se não houver erro.</summary>
    public static async Task<OracleException> FalhaAsync(Usuario usuario, string sql)
    {
        await using OracleConnection conexao = await AbrirAsync(usuario);
        await using OracleTransaction transacao = conexao.BeginTransaction();
        await using var comando = new OracleCommand(sql, conexao) { Transaction = transacao };
        OracleException erro = await Assert.ThrowsAsync<OracleException>(() => comando.ExecuteNonQueryAsync());
        await transacao.RollbackAsync();
        return erro;
    }
}

/// <summary>Só roda com a connection string do usuário pedido (as mesmas variáveis da paridade e da sombra).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GarantiaOracleFactAttribute : FactAttribute
{
    public GarantiaOracleFactAttribute(Usuario usuario)
    {
        string variavel = usuario == Usuario.Aplicacao ? ParidadeTheoryAttribute.Variavel : SombraOracleTheoryAttribute.Variavel;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variavel)))
        {
            Skip = $"defina {variavel} para conferir os grants do Oracle";
        }
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class GarantiaK08FactAttribute : FactAttribute
{
    public GarantiaK08FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ParidadeTheoryAttribute.Variavel)) ||
            Environment.GetEnvironmentVariable("NFSE_PARIDADE_K08") != "1")
        {
            Skip = "cancela a nota 27/5: defina NFSE_PARIDADE_K08=1 sobre a linha de base (tests/paridade/README.md)";
        }
    }
}

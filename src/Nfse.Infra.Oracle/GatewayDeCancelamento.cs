using System.Data;
using Nfse.Aplicacao.Cancelamento;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;
using Oracle.ManagedDataAccess.Client;

namespace Nfse.Infra.Oracle;

/// <summary>
/// Chama <c>NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar</c> numa transação e faz o commit uma vez (ADR-0003). Os
/// códigos <c>E05x</c> do PL/SQL são traduzidos aqui para o domínio, que não os conhece.
/// </summary>
public sealed class GatewayDeCancelamento(FabricaDeConexoes conexoes) : IGatewayDeCancelamento
{
    // Espera do lock (5 s, no wrapper) + execução, abaixo do timeout de 10 s da fachada (ADR-0007, ADR-0008).
    private const int TimeoutEmSegundos = 8;

    public async Task<ResultadoDoGateway> CancelarAsync(long prestadorId, long numero, string motivo, CancellationToken cancellationToken)
    {
        await using var conexao = (OracleConnection)await conexoes.AbrirAsync(cancellationToken).ConfigureAwait(false);
        await using var transacao = (OracleTransaction)await conexao.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var comando = new OracleCommand("NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar", conexao)
        {
            CommandType = CommandType.StoredProcedure,
            BindByName = true,
            CommandTimeout = TimeoutEmSegundos,
            Transaction = transacao,
        };

        comando.Parameters.Add("p_prestador_id", OracleDbType.Int64).Value = prestadorId;
        comando.Parameters.Add("p_numero", OracleDbType.Int64).Value = numero;
        comando.Parameters.Add("p_motivo", OracleDbType.Varchar2).Value = motivo;
        OracleParameter erro = Saida(comando, "o_codigo_erro", OracleDbType.Varchar2, 10);
        OracleParameter situacao = Saida(comando, "o_situacao_antes", OracleDbType.Varchar2, 1);
        OracleParameter competencia = Saida(comando, "o_competencia", OracleDbType.Date);
        OracleParameter guiaPaga = Saida(comando, "o_guia_paga_antes", OracleDbType.Int32);
        OracleParameter agora = Saida(comando, "o_agora_banco", OracleDbType.Date);
        OracleParameter dataCancelamento = Saida(comando, "o_dt_cancelamento", OracleDbType.Date);

        await comando.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transacao.CommitAsync(cancellationToken).ConfigureAwait(false);

        string? codigo = Texto(erro);
        string? situacaoAntes = Texto(situacao);
        EstadoParaCancelamento? estado = situacaoAntes is null || Data(competencia) is not { } comp
            ? null
            : new EstadoParaCancelamento(Situacao(situacaoAntes), Competencia.De(DateOnly.FromDateTime(comp)), Inteiro(guiaPaga) > 0);

        DecisaoDeCancelamento decisao = codigo switch
        {
            null => situacaoAntes == "C" ? DecisaoDeCancelamento.JaCancelada : DecisaoDeCancelamento.Permitido,
            "E050" => DecisaoDeCancelamento.NotaNaoEncontrada,
            "E051" => DecisaoDeCancelamento.NotaSubstituida,
            "E052" => DecisaoDeCancelamento.PrazoExpirado,
            "E053" => DecisaoDeCancelamento.GuiaPaga,
            _ => throw new InvalidOperationException($"Código de cancelamento desconhecido: {codigo}."),
        };

        return new ResultadoDoGateway(decisao, estado, Data(agora), Data(dataCancelamento));
    }

    private static OracleParameter Saida(OracleCommand comando, string nome, OracleDbType tipo, int tamanho = 0)
    {
        OracleParameter parametro = comando.Parameters.Add(nome, tipo);
        parametro.Direction = ParameterDirection.Output;
        if (tamanho > 0)
        {
            parametro.Size = tamanho;
        }

        return parametro;
    }

    private static string? Texto(OracleParameter p) =>
        p.Value is global::Oracle.ManagedDataAccess.Types.OracleString { IsNull: false } s ? s.Value : null;

    private static DateTime? Data(OracleParameter p) =>
        p.Value is global::Oracle.ManagedDataAccess.Types.OracleDate { IsNull: false } d ? d.Value : null;

    private static int Inteiro(OracleParameter p) =>
        p.Value is global::Oracle.ManagedDataAccess.Types.OracleDecimal { IsNull: false } d ? d.ToInt32() : 0;

    private static SituacaoNota Situacao(string codigo) => codigo switch
    {
        "C" => SituacaoNota.Cancelada,
        "S" => SituacaoNota.Substituida,
        _ => SituacaoNota.Normal,
    };
}

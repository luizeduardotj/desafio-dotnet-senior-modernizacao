using Oracle.ManagedDataAccess.OpenTelemetry;
using OpenTelemetry.Trace;

namespace Nfse.Infra.Oracle;

/// <summary>
/// Spans do ODP.NET no trace do serviço novo (fachada → novo → Oracle, ADR-0012), pela extensão oficial da Oracle.
/// O ODP.NET emite pelo <c>ActivitySource</c> <see cref="Fonte"/>; a extensão liga essa fonte e as opções abaixo.
/// </summary>
/// <remarks>
/// Nenhum valor de bind entra no span: o ODP.NET não tem opção para capturá-los. O texto do SQL também fica fora;
/// <c>db.statement</c> só sai para stored procedure, como o bloco que o ODP.NET monta com placeholders
/// (<c>Begin NFSE_MODERNO.PKG_CANCELAMENTO_API.cancelar ("P_NUMERO"=&gt;:v1, ...);End;</c>), conferido no Tempo. Ficam
/// desligados ainda os atributos da conexão (usuário, servidor) e a exceção no span, porque a mensagem de um
/// <c>ORA-</c> pode citar valores.
/// </remarks>
public static class TelemetriaDoOracle
{
    /// <summary>Nome do <c>ActivitySource</c> do ODP.NET Core 23.x (<c>OracleActivitySource</c>).</summary>
    public const string Fonte = "Oracle.ManagedDataAccess.Core";

    public static TracerProviderBuilder AdicionarTelemetriaDoOracle(this TracerProviderBuilder tracing) =>
        tracing.AddOracleDataProviderInstrumentation(ConfigurarOpcoes);

    /// <summary>Explícito mesmo onde coincide com o padrão, para uma troca de versão não mudar o que sai.</summary>
    public static void ConfigurarOpcoes(OracleDataProviderInstrumentationOptions opcoes)
    {
        ArgumentNullException.ThrowIfNull(opcoes);
        opcoes.SetDbStatementForText = false;
        opcoes.SetDbStatementForStoredProcedure = true;
        opcoes.EnableConnectionLevelAttributes = false;
        opcoes.RecordException = false;
        opcoes.InstrumentOracleDataReaderRead = false;
        opcoes.EnableSqlIdTracing = false;
        opcoes.AddDBInfoToDisplayName = false;
    }
}

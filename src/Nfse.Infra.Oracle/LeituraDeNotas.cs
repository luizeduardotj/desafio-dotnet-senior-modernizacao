using System.Text;
using Dapper;
using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Documentos;
using Nfse.Dominio.Notas;

namespace Nfse.Infra.Oracle;

/// <summary>
/// Leitura das notas pela view <c>NFSE_MODERNO.V_NFSE</c>. A tradução das convenções do legado
/// (<c>N/C/S</c>, <c>ISS_RETIDO S/N</c>, documento como texto) acontece aqui, nunca na view (ADR-0002).
/// </summary>
public sealed class LeituraDeNotas(FabricaDeConexoes conexoes) : ILeituraDeNotas
{
    private const string Colunas =
        "n.ID, n.PRESTADOR_ID, n.NUMERO, n.CODIGO_VERIFICACAO, n.DT_EMISSAO, n.COMPETENCIA, n.ITEM_SERVICO, " +
        "n.DISCRIMINACAO, n.VALOR_SERVICOS, n.VALOR_DEDUCOES, n.BASE_CALCULO, n.ALIQUOTA, n.VALOR_ISS, n.ISS_RETIDO, " +
        "n.TOMADOR_DOCUMENTO, n.TOMADOR_RAZAO, n.SITUACAO, n.DT_CANCELAMENTO, n.NUMERO_SUBSTITUIDORA, " +
        "n.PRESTADOR_DOCUMENTO, n.PRESTADOR_IM, n.PRESTADOR_RAZAO";

    public Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasPorFaixaAsync(
        FiltroDeNotasEmitidas filtro, int deslocamento, int limite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return PorFaixaAsync(Condicoes(filtro), Parametros(filtro), deslocamento, limite, cancellationToken);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarEmitidasAposAsync(
        FiltroDeNotasEmitidas filtro, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return AposAsync(Condicoes(filtro), Parametros(filtro), apos, limite, cancellationToken);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarTomadasPorFaixaAsync(
        FiltroDeNotasTomadas filtro, int deslocamento, int limite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return PorFaixaAsync(Condicoes(filtro), Parametros(filtro), deslocamento, limite, cancellationToken);
    }

    public Task<IReadOnlyList<NotaFiscalLida>> ListarTomadasAposAsync(
        FiltroDeNotasTomadas filtro, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        return AposAsync(Condicoes(filtro), Parametros(filtro), apos, limite, cancellationToken);
    }

    // Mesma paginação do legado (ROWNUM dentro de ROWNUM, ordenação na subconsulta interna).
    private Task<IReadOnlyList<NotaFiscalLida>> PorFaixaAsync(
        string condicoes, DynamicParameters parametros, int deslocamento, int limite, CancellationToken cancellationToken)
    {
        parametros.Add("limite", limite);
        parametros.Add("deslocamento", deslocamento);
        string sql =
            "SELECT * FROM (SELECT a.*, ROWNUM RN FROM (" +
            "SELECT " + Colunas + " FROM NFSE_MODERNO.V_NFSE n WHERE " + condicoes +
            " ORDER BY n.DT_EMISSAO, n.ID) a WHERE ROWNUM <= :limite) WHERE RN > :deslocamento";
        return ConsultarAsync(sql, parametros, cancellationToken);
    }

    private Task<IReadOnlyList<NotaFiscalLida>> AposAsync(
        string condicoes, DynamicParameters parametros, PosicaoNaListagem? apos, int limite, CancellationToken cancellationToken)
    {
        parametros.Add("limite", limite);
        if (apos is { } posicao)
        {
            condicoes += " AND (n.DT_EMISSAO > :aposData OR (n.DT_EMISSAO = :aposData AND n.ID > :aposId))";
            parametros.Add("aposData", posicao.DataEmissao);
            parametros.Add("aposId", posicao.Id);
        }

        string sql =
            "SELECT " + Colunas + " FROM NFSE_MODERNO.V_NFSE n WHERE " + condicoes +
            " ORDER BY n.DT_EMISSAO, n.ID FETCH FIRST :limite ROWS ONLY";
        return ConsultarAsync(sql, parametros, cancellationToken);
    }

    private static string Condicoes(FiltroDeNotasEmitidas filtro)
    {
        var sql = new StringBuilder("n.PRESTADOR_ID = :prestador AND n.DT_EMISSAO >= :inicio AND n.DT_EMISSAO < :fim");
        if (filtro.CompetenciaAte is not null)
        {
            sql.Append(" AND n.COMPETENCIA <= :competenciaAte");
        }

        if (filtro.TomadorDigitos is not null)
        {
            sql.Append(" AND n.TOMADOR_DOCUMENTO = :tomador");
        }

        if (filtro.Situacao is not null)
        {
            sql.Append(" AND n.SITUACAO = :situacao");
        }

        return sql.ToString();
    }

    // O legado faz TRUNC(DT_EMISSAO) >= :ini AND TRUNC(DT_EMISSAO) < :fim com bind numérico e /*+ FULL(n) */.
    // Com ini e fim à meia-noite, a faixa sobre DT_EMISSAO é equivalente e usa IX_NFSE_TOMADOR_DT.
    private static string Condicoes(FiltroDeNotasTomadas filtro) =>
        "n.TOMADOR_DOCUMENTO = :tomador AND n.DT_EMISSAO >= :inicio AND n.DT_EMISSAO < :fim" +
        (filtro.Situacao is null ? "" : " AND n.SITUACAO = :situacao");

    private static DynamicParameters Parametros(FiltroDeNotasTomadas filtro)
    {
        var parametros = new DynamicParameters();
        // Texto contra VARCHAR2 (C15): sem a colisão CPF × CNPJ do bind numérico do legado.
        parametros.Add("tomador", filtro.TomadorDigitos, System.Data.DbType.String);
        parametros.Add("inicio", filtro.InicioInclusivo);
        parametros.Add("fim", filtro.FimExclusivo);
        if (filtro.Situacao is { } situacao)
        {
            parametros.Add("situacao", CodigoDaSituacao(situacao), System.Data.DbType.String);
        }

        return parametros;
    }

    private static DynamicParameters Parametros(FiltroDeNotasEmitidas filtro)
    {
        var parametros = new DynamicParameters();
        parametros.Add("prestador", filtro.PrestadorId);
        parametros.Add("inicio", filtro.InicioInclusivo);
        parametros.Add("fim", filtro.FimExclusivo);
        if (filtro.CompetenciaAte is { } competenciaAte)
        {
            parametros.Add("competenciaAte", competenciaAte);
        }

        if (filtro.TomadorDigitos is { } tomador)
        {
            // Texto contra VARCHAR2: usa o índice e não colide CPF com CNPJ de mesmo valor numérico (C15).
            parametros.Add("tomador", tomador, System.Data.DbType.String);
        }

        if (filtro.Situacao is { } situacao)
        {
            parametros.Add("situacao", CodigoDaSituacao(situacao), System.Data.DbType.String);
        }

        return parametros;
    }

    private async Task<IReadOnlyList<NotaFiscalLida>> ConsultarAsync(string sql, DynamicParameters parametros, CancellationToken cancellationToken)
    {
        await using var conexao = await conexoes.AbrirAsync(cancellationToken).ConfigureAwait(false);
        var linhas = await conexao.QueryAsync<LinhaNota>(
            new CommandDefinition(sql, parametros, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. linhas.Select(Converter)];
    }

    private static NotaFiscalLida Converter(LinhaNota l) => new(
        Id: (long)l.ID,
        Numero: (long)l.NUMERO,
        CodigoVerificacao: l.CODIGO_VERIFICACAO,
        DataEmissao: l.DT_EMISSAO ?? throw new InvalidOperationException($"NFS-e {l.ID} sem DT_EMISSAO."),
        Competencia: l.COMPETENCIA is { } c ? DateOnly.FromDateTime(c) : null,
        ItemServico: l.ITEM_SERVICO,
        Discriminacao: l.DISCRIMINACAO,
        ValorServicos: l.VALOR_SERVICOS,
        ValorDeducoes: l.VALOR_DEDUCOES,
        BaseCalculo: l.BASE_CALCULO,
        Aliquota: l.ALIQUOTA,
        ValorIss: l.VALOR_ISS,
        IssRetido: l.ISS_RETIDO == "S",
        Situacao: Situacao(l.SITUACAO),
        DataCancelamento: l.DT_CANCELAMENTO,
        NumeroSubstituidora: l.NUMERO_SUBSTITUIDORA is { } s ? (long)s : null,
        Prestador: new PrestadorLido((long)l.PRESTADOR_ID, l.PRESTADOR_IM ?? "", l.PRESTADOR_DOCUMENTO, l.PRESTADOR_RAZAO, Ativo: true),
        TomadorDocumento: string.IsNullOrEmpty(l.TOMADOR_DOCUMENTO) ? null : Documento.Reconstituir(l.TOMADOR_DOCUMENTO),
        TomadorRazaoSocial: l.TOMADOR_RAZAO);

    // Valor desconhecido é tratado como normal: é o que o legado faz ao montar a resposta (só C e S têm efeito).
    private static SituacaoNota Situacao(string? codigo) => codigo switch
    {
        "C" => SituacaoNota.Cancelada,
        "S" => SituacaoNota.Substituida,
        _ => SituacaoNota.Normal,
    };

    private static string CodigoDaSituacao(SituacaoNota situacao) => situacao switch
    {
        SituacaoNota.Cancelada => "C",
        SituacaoNota.Substituida => "S",
        _ => "N",
    };

#pragma warning disable CA1812, CA1707 // instanciada pelo Dapper; nomes iguais às colunas
    private sealed class LinhaNota
    {
        public decimal ID { get; set; }
        public decimal PRESTADOR_ID { get; set; }
        public decimal NUMERO { get; set; }
        public string? CODIGO_VERIFICACAO { get; set; }
        public DateTime? DT_EMISSAO { get; set; }
        public DateTime? COMPETENCIA { get; set; }
        public string? ITEM_SERVICO { get; set; }
        public string? DISCRIMINACAO { get; set; }
        public decimal? VALOR_SERVICOS { get; set; }
        public decimal? VALOR_DEDUCOES { get; set; }
        public decimal? BASE_CALCULO { get; set; }
        public decimal? ALIQUOTA { get; set; }
        public decimal? VALOR_ISS { get; set; }
        public string? ISS_RETIDO { get; set; }
        public string? TOMADOR_DOCUMENTO { get; set; }
        public string? TOMADOR_RAZAO { get; set; }
        public string? SITUACAO { get; set; }
        public DateTime? DT_CANCELAMENTO { get; set; }
        public decimal? NUMERO_SUBSTITUIDORA { get; set; }
        public string? PRESTADOR_DOCUMENTO { get; set; }
        public string? PRESTADOR_IM { get; set; }
        public string? PRESTADOR_RAZAO { get; set; }
    }
#pragma warning restore CA1812, CA1707
}

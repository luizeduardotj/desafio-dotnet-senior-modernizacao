using System.ComponentModel.DataAnnotations;

namespace Nfse.Fachada.Configuracao;

/// <summary>Seção <c>Legado</c> (ex.: <c>Legado__Url=http://webservices:8080</c>).</summary>
public sealed class OpcoesLegado
{
    public const string Secao = "Legado";

    [Required]
    public Uri? Url { get; set; }

    /// <summary>Inatividade máxima na chamada ao legado. O legado não tem limite próprio; o padrão é o do YARP.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
}

/// <summary>Seção <c>Novo</c>. Sem <c>Url</c>, o destino <c>novo</c> cai no legado.</summary>
public sealed class OpcoesNovo
{
    public const string Secao = "Novo";

    public Uri? Url { get; set; }

    public TimeSpan TimeoutLeitura { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan TimeoutCancelamento { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>Seção <c>Admin</c>: porta interna e token do kill switch nível 2.</summary>
public sealed class OpcoesAdmin
{
    public const string Secao = "Admin";

    [Range(1, 65535)]
    public int Porta { get; set; } = 8081;

    /// <summary>Vem de segredo (<c>Admin__Token</c>). Vazio desliga os endpoints administrativos.</summary>
    public string? Token { get; set; }
}

/// <summary>Seção <c>Fachada</c>.</summary>
public sealed class OpcoesFachada
{
    public const string Secao = "Fachada";

    /// <summary>Limite de bufferização das operações em escopo (ADR-0008). Acima dele, legado em streaming.</summary>
    public const int LimiteDoCorpo = 1024 * 1024;

    [Range(1, 65535)]
    public int Porta { get; set; } = 8080;

    /// <summary>Arquivo JSON com a seção <c>Migracao</c>, observado para recarga.</summary>
    public string ArquivoDeFlags { get; set; } = "flags.json";
}

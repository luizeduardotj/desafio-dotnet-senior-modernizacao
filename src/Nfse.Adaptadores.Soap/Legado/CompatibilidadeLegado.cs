using System.Globalization;
using System.Text;
using System.Xml;
using Nfse.Dominio.Documentos;

namespace Nfse.Adaptadores.Soap.Legado;

/// <summary>
/// Regras de leitura do pedido que reproduzem o legado de propósito. Cada uma cita o ID do catálogo da ADR-0004;
/// nenhuma delas vale para o domínio nem para a API REST.
/// </summary>
internal static class CompatibilidadeLegado
{
    public const int TamanhoDaPagina = 50;

    private static readonly XmlReaderSettings Seguro = new()
    {
        // C28: o legado processa DTD interna (XmlDocument.LoadXml). Aqui é proibida: vira E001 (decisão consciente).
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    private static readonly TimeZoneInfo SaoPaulo = ResolverSaoPaulo();

    /// <summary>Carrega o XML de negócio; <see cref="XmlException"/> vira E001 em quem chama.</summary>
    public static XmlDocument Carregar(string xml)
    {
        var documento = new XmlDocument { XmlResolver = null };
        using var leitor = XmlReader.Create(new StringReader(xml), Seguro);
        documento.Load(leitor);
        return documento;
    }

    /// <summary>C23: caminho sem namespace, <c>InnerText.Trim()</c>; ausente devolve <see langword="null"/>.</summary>
    public static string? Texto(XmlNode no, string caminho) => no.SelectSingleNode(caminho)?.InnerText.Trim();

    /// <summary>C26: <c>CpfCnpj/Cnpj</c> tem precedência sobre <c>CpfCnpj/Cpf</c>.</summary>
    public static string? LerDocumento(XmlNode? no)
    {
        if (no is null)
        {
            return null;
        }

        string? documento = Texto(no, "CpfCnpj/Cnpj");
        return string.IsNullOrEmpty(documento) ? Texto(no, "CpfCnpj/Cpf") : documento;
    }

    /// <summary>C12: remove tudo que não é dígito (letras inclusive) antes de validar.</summary>
    public static string SomenteDigitos(string? texto)
    {
        if (texto is null)
        {
            return "";
        }

        var sb = new StringBuilder(texto.Length);
        foreach (char c in texto)
        {
            if (char.IsDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>C12: decide CPF ou CNPJ pelo tamanho, ignorando a tag; DV conferido pelo domínio.</summary>
    public static bool ValidarCpfOuCnpj(string? texto) => Documento.Criar(SomenteDigitos(texto)).Sucesso;

    /// <summary>
    /// C03/C04: datas por <c>DateTime.TryParse</c> invariante (aceita formatos não ISO), hora descartada, e
    /// <c>(fim − início) &gt; 31 dias</c> é inválido. Data com offset é convertida para São Paulo antes de truncar,
    /// sem depender do fuso do processo.
    /// </summary>
    public static bool LerPeriodo(XmlNode raiz, out DateTime inicio, out DateTime fim)
    {
        inicio = DateTime.MinValue;
        fim = DateTime.MinValue;
        if (!LerData(Texto(raiz, "PeriodoEmissao/DataInicial"), out inicio) || !LerData(Texto(raiz, "PeriodoEmissao/DataFinal"), out fim))
        {
            return false;
        }

        inicio = inicio.Date;
        fim = fim.Date;
        return inicio <= fim && (fim - inicio).TotalDays <= 31;
    }

    /// <summary>C05: página ausente, inválida ou menor que 1 vira 1.</summary>
    public static int LerPagina(XmlNode raiz)
    {
        if (!int.TryParse(Texto(raiz, "Pagina"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pagina) || pagina < 1)
        {
            return 1;
        }

        return pagina;
    }

    /// <summary>C07/C27: faixa da página com a mesma aritmética <c>int</c> sem checagem do legado.</summary>
    public static (int Deslocamento, int Limite) Faixa(int pagina) =>
        (unchecked((pagina - 1) * TamanhoDaPagina), unchecked((pagina * TamanhoDaPagina) + 1));

    private static bool LerData(string? texto, out DateTime data)
    {
        if (!DateTime.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out data))
        {
            return false;
        }

        if (data.Kind != DateTimeKind.Unspecified &&
            DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset comOffset))
        {
            data = TimeZoneInfo.ConvertTime(comOffset, SaoPaulo).DateTime;
        }

        return true;
    }

    private static TimeZoneInfo ResolverSaoPaulo()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            // Sem tzdata na imagem: o Brasil não tem horário de verão desde 2019.
            return TimeZoneInfo.CreateCustomTimeZone("America/Sao_Paulo", TimeSpan.FromHours(-3), "America/Sao_Paulo", "BRT");
        }
    }
}

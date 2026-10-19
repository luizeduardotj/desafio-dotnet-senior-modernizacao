using System.Globalization;
using System.Security;
using System.Text;
using Nfse.Aplicacao.Consultas;
using Nfse.Dominio.Notas;

namespace Nfse.Adaptadores.Soap.Legado;

/// <summary>
/// Monta o XML de negócio byte a byte como <c>legado/src/NotaCarioca.Servicos/XmlNfseBuilder.cs</c>: mesma ordem de
/// elementos, mesmo escape, mesmos formatos. Não "corrija" nada aqui sem um ID do catálogo (ADR-0004).
/// </summary>
internal static class SerializadorLegado
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Esc(string? s) => s is null ? "" : SecurityElement.Escape(s);

    public static string FmtData(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv);

    public static string FmtCompetencia(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToString("yyyy-MM-01'T'00:00:00", Inv);

    public static string FmtValor(decimal v) => v.ToString("0.00", Inv);

    public static string FmtAliquota(decimal a) => a.ToString("0.####", Inv);

    public static string RespostaErro(string raiz, string codigo) =>
        "<" + raiz + "><ListaMensagemRetorno><MensagemRetorno><Codigo>" + codigo + "</Codigo><Mensagem>" +
        Esc(CatalogoDeRetornos.Mensagem(codigo)) + "</Mensagem><Correcao>" + Esc(CatalogoDeRetornos.Correcao(codigo)) +
        "</Correcao></MensagemRetorno></ListaMensagemRetorno></" + raiz + ">";

    /// <summary>C07: exibe até 50 e indica <c>ProximaPagina</c> pela 51ª linha buscada.</summary>
    public static string Pagina(string raiz, IReadOnlyList<NotaFiscalLida> notas, int tamanhoDaPagina)
    {
        int exibir = Math.Min(notas.Count, tamanhoDaPagina);
        var sb = new StringBuilder();
        sb.Append('<').Append(raiz).Append("><ListaNfse>");
        for (int i = 0; i < exibir; i++)
        {
            CompNfse(sb, notas[i]);
        }

        sb.Append("</ListaNfse><ProximaPagina>")
          .Append(notas.Count > tamanhoDaPagina ? "true" : "false")
          .Append("</ProximaPagina></").Append(raiz).Append('>');
        return sb.ToString();
    }

    private static void CompNfse(StringBuilder sb, NotaFiscalLida n)
    {
        sb.Append("<CompNfse><Nfse><InfNfse>");
        sb.Append("<Numero>").Append(n.Numero.ToString(Inv)).Append("</Numero>");
        sb.Append("<CodigoVerificacao>").Append(Esc(n.CodigoVerificacao)).Append("</CodigoVerificacao>");
        sb.Append("<DataEmissao>").Append(FmtData(n.DataEmissao)).Append("</DataEmissao>");
        sb.Append("<Competencia>").Append(FmtCompetencia(Obrigatorio(n.Competencia, "COMPETENCIA"))).Append("</Competencia>");
        sb.Append("<Servico><Valores>");
        sb.Append("<ValorServicos>").Append(FmtValor(Obrigatorio(n.ValorServicos, "VALOR_SERVICOS"))).Append("</ValorServicos>");
        sb.Append("<ValorDeducoes>").Append(FmtValor(Obrigatorio(n.ValorDeducoes, "VALOR_DEDUCOES"))).Append("</ValorDeducoes>");
        sb.Append("<BaseCalculo>").Append(FmtValor(Obrigatorio(n.BaseCalculo, "BASE_CALCULO"))).Append("</BaseCalculo>");
        sb.Append("<Aliquota>").Append(FmtAliquota(Obrigatorio(n.Aliquota, "ALIQUOTA"))).Append("</Aliquota>");
        sb.Append("<ValorIss>").Append(FmtValor(Obrigatorio(n.ValorIss, "VALOR_ISS"))).Append("</ValorIss>");
        sb.Append("<IssRetido>").Append(n.IssRetido ? "1" : "2").Append("</IssRetido>");
        sb.Append("</Valores>");
        sb.Append("<ItemListaServico>").Append(Esc(n.ItemServico)).Append("</ItemListaServico>");
        sb.Append("<Discriminacao>").Append(Esc(n.Discriminacao)).Append("</Discriminacao>");
        sb.Append("</Servico>");
        sb.Append("<PrestadorServico><IdentificacaoPrestador>");
        // C11: o prestador sai sempre na tag Cnpj, mesmo se o documento for CPF.
        sb.Append("<Cnpj>").Append(Esc(n.Prestador.Documento)).Append("</Cnpj>");
        sb.Append("<InscricaoMunicipal>").Append(Esc(n.Prestador.InscricaoMunicipal)).Append("</InscricaoMunicipal>");
        sb.Append("</IdentificacaoPrestador>");
        sb.Append("<RazaoSocial>").Append(Esc(n.Prestador.RazaoSocial)).Append("</RazaoSocial>");
        sb.Append("</PrestadorServico>");

        // C10: a tag sai pelo tamanho do texto gravado, o valor sai como inteiro (perde zeros à esquerda).
        string documentoGravado = n.TomadorDocumento?.Digitos
            ?? throw new InvalidOperationException($"NFS-e {n.Id} sem TOMADOR_DOCUMENTO.");
        string tag = documentoGravado.Length > 11 ? "Cnpj" : "Cpf";
        string valor = long.Parse(documentoGravado, Inv).ToString(Inv);
        sb.Append("<TomadorServico><IdentificacaoTomador><CpfCnpj>");
        sb.Append('<').Append(tag).Append('>').Append(valor).Append("</").Append(tag).Append('>');
        sb.Append("</CpfCnpj></IdentificacaoTomador>");
        sb.Append("<RazaoSocial>").Append(Esc(n.TomadorRazaoSocial)).Append("</RazaoSocial>");
        sb.Append("</TomadorServico>");
        sb.Append("</InfNfse>");

        if (n.Situacao == SituacaoNota.Cancelada && n.DataCancelamento is { } cancelamento)
        {
            sb.Append("<NfseCancelamento><Confirmacao><DataHora>").Append(FmtData(cancelamento))
              .Append("</DataHora></Confirmacao></NfseCancelamento>");
        }

        if (n.Situacao == SituacaoNota.Substituida && n.NumeroSubstituidora is { } substituidora)
        {
            sb.Append("<NfseSubstituicao><NfseSubstituidora>").Append(substituidora.ToString(Inv))
              .Append("</NfseSubstituidora></NfseSubstituicao>");
        }

        sb.Append("</Nfse></CompNfse>");
    }

    // O legado lança ao converter DBNull e a página inteira vira Fault; aqui também.
    private static T Obrigatorio<T>(T? valor, string coluna) where T : struct =>
        valor ?? throw new InvalidOperationException($"Coluna {coluna} nula.");
}

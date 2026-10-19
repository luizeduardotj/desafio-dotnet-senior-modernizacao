using Nfse.Fachada.Sombra;

namespace Nfse.Fachada.Testes.Sombra;

/// <summary>ADR-0009, seção 4, e ADR-0012: nada em claro além de número da nota, período e página.</summary>
public sealed class PseudonimizadorTests
{
    private const string Chave = "chave";

    [Fact]
    public void Documentos_e_IM_viram_HMAC_e_periodo_e_pagina_ficam_em_claro()
    {
        string? pedido = Pseudonimizador.Requisicao(Exemplos.Bytes("04-consultar-nfse.xml"), Chave);

        Assert.NotNull(pedido);
        Assert.DoesNotContain("11100003000113", pedido, StringComparison.Ordinal);
        Assert.DoesNotContain("10000001", pedido, StringComparison.Ordinal);
        Assert.Contains($"<Cnpj>{Pseudonimizador.Hmac("11100003000113", "chave"u8.ToArray())}</Cnpj>", pedido, StringComparison.Ordinal);
        Assert.Contains("<DataInicial>2026-10-01</DataInicial><DataFinal>2026-10-31</DataFinal>", pedido, StringComparison.Ordinal);
        Assert.Contains("<Pagina>1</Pagina>", pedido, StringComparison.Ordinal);
    }

    [Fact]
    public void Qualquer_campo_fora_da_lista_de_permissao_sai_pseudonimizado()
    {
        byte[] envelope = "<e><inputXML>&lt;X a=\"Fulano\"&gt;&lt;RazaoSocial&gt;FULANO MEI&lt;/RazaoSocial&gt;&lt;Apelido&gt;Fulano&lt;/Apelido&gt;&lt;!-- Fulano --&gt;&lt;/X&gt;</inputXML></e>"u8.ToArray();

        string? pedido = Pseudonimizador.Requisicao(envelope, Chave);

        Assert.NotNull(pedido);
        Assert.DoesNotContain("Fulano", pedido, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^<X a=\"hmac:[0-9a-f]{64}\"><RazaoSocial>hmac:[0-9a-f]{64}</RazaoSocial><Apelido>hmac:[0-9a-f]{64}</Apelido></X>$", pedido);
    }

    [Fact]
    public void Mesmo_valor_e_mesma_chave_dao_o_mesmo_HMAC_e_outra_chave_muda()
    {
        Assert.Equal(Pseudonimizador.Hmac("10000001", "a"u8.ToArray()), Pseudonimizador.Hmac("10000001", "a"u8.ToArray()));
        Assert.NotEqual(Pseudonimizador.Hmac("10000001", "a"u8.ToArray()), Pseudonimizador.Hmac("10000001", "b"u8.ToArray()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sem_chave_nada_sai(string? chave) =>
        Assert.Null(Pseudonimizador.Requisicao(Exemplos.Bytes("04-consultar-nfse.xml"), chave));

    [Fact]
    public void Pedido_com_DTD_nada_sai() =>
        Assert.Null(Pseudonimizador.Requisicao("<!DOCTYPE x [<!ENTITY a \"10000001\">]><e><inputXML>&a;</inputXML></e>"u8, Chave));
}

using Nfse.Fachada.Sombra;

namespace Nfse.Fachada.Testes.Sombra;

public sealed class CapturaDaRespostaTests
{
    [Fact]
    public async Task Cliente_recebe_tudo_e_a_copia_e_igual()
    {
        using var cliente = new MemoryStream();
        await using var captura = new CapturaDaResposta(cliente, 10);

        await captura.WriteAsync("abcde"u8.ToArray());
        captura.Write("fgh"u8.ToArray(), 0, 3);

        Assert.Equal("abcdefgh"u8.ToArray(), cliente.ToArray());
        Assert.Equal("abcdefgh"u8.ToArray(), captura.Bytes);
        Assert.False(captura.Excedeu);
    }

    [Fact]
    public async Task Acima_do_limite_abandona_a_copia_mas_nao_o_cliente()
    {
        using var cliente = new MemoryStream();
        await using var captura = new CapturaDaResposta(cliente, 4);

        await captura.WriteAsync("abc"u8.ToArray());
        await captura.WriteAsync("de"u8.ToArray());
        await captura.WriteAsync("f"u8.ToArray());

        Assert.Equal("abcdef"u8.ToArray(), cliente.ToArray());
        Assert.Null(captura.Bytes);
        Assert.True(captura.Excedeu);
    }
}

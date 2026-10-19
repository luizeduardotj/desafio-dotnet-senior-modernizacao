using Nfse.Dominio.Comum;
using Nfse.Dominio.Documentos;

namespace Nfse.Dominio.Testes;

public class DocumentoTests
{
    [Theory]
    [InlineData("52998224725", TipoDocumento.Cpf)]
    [InlineData("01234567890", TipoDocumento.Cpf)]
    [InlineData("11444777000161", TipoDocumento.Cnpj)]
    [InlineData("01234567000195", TipoDocumento.Cnpj)]
    public void Aceita_documento_valido_preservando_zeros(string texto, TipoDocumento tipo)
    {
        var resultado = Documento.Criar(texto);

        Assert.True(resultado.Sucesso);
        Assert.Equal(texto, resultado.Valor.Digitos);
        Assert.Equal(tipo, resultado.Valor.Tipo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("529.982.247-25")]          // máscara: o legado aceita (C12); o domínio não
    [InlineData("5299822472A5")]            // letra: o legado remove em silêncio (C12)
    [InlineData("52998224726")]             // DV errado
    [InlineData("11111111111")]             // todos iguais
    [InlineData("00000000000000")]
    [InlineData("1234567000195")]           // 13 dígitos: CNPJ que perdeu o zero (C10)
    [InlineData("123456789012345")]
    public void Rejeita_documento_invalido(string? texto)
    {
        var resultado = Documento.Criar(texto);

        Assert.False(resultado.Sucesso);
        Assert.IsType<DocumentoInvalido>(resultado.Erro);
    }

    [Fact]
    public void Reconstituir_aceita_documento_gravado_sem_dv_valido()
    {
        // seed de volume grava CNPJ sem DV; a nota precisa continuar legível
        var documento = Documento.Reconstituir("12345678000100");

        Assert.Equal(TipoDocumento.Cnpj, documento.Tipo);
        Assert.Equal("12345678000100", documento.Digitos);
    }

    [Fact]
    public void ToString_nao_expoe_os_digitos()
    {
        var documento = Documento.Criar("52998224725").Valor;

        Assert.DoesNotContain("529", documento.ToString(), StringComparison.Ordinal);
    }
}

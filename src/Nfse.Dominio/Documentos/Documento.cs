using Nfse.Dominio.Comum;

namespace Nfse.Dominio.Documentos;

/// <summary>
/// CPF ou CNPJ, sempre como texto com 11 ou 14 dígitos (zeros à esquerda preservados).
/// </summary>
/// <remarks>
/// Entrada de usuário passa por <see cref="Criar"/>, que exige só dígitos e dígitos verificadores válidos.
/// Valor lido do banco passa por <see cref="Reconstituir"/>: a base do legado tem documentos sem DV válido
/// (seed de volume), e uma nota gravada não pode deixar de ser exibida por isso.
/// </remarks>
public sealed record Documento
{
    private Documento(string digitos, TipoDocumento tipo)
    {
        Digitos = digitos;
        Tipo = tipo;
    }

    public string Digitos { get; }

    public TipoDocumento Tipo { get; }

    public static Resultado<Documento> Criar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
            return new DocumentoInvalido("vazio");
        if (!SoDigitos(texto))
            return new DocumentoInvalido("deve conter só dígitos");

        return texto.Length switch
        {
            11 when CpfValido(texto) => new Documento(texto, TipoDocumento.Cpf),
            14 when CnpjValido(texto) => new Documento(texto, TipoDocumento.Cnpj),
            11 or 14 => new DocumentoInvalido("dígitos verificadores não conferem"),
            _ => new DocumentoInvalido("deve ter 11 (CPF) ou 14 (CNPJ) dígitos"),
        };
    }

    public static Documento Reconstituir(string digitos)
    {
        ArgumentException.ThrowIfNullOrEmpty(digitos);
        var tipo = digitos.Length > 11 ? TipoDocumento.Cnpj : TipoDocumento.Cpf;
        return new Documento(digitos, tipo);
    }

    public override string ToString() => Tipo == TipoDocumento.Cpf ? "CPF ***" : "CNPJ ***";

    private static bool SoDigitos(string texto)
    {
        foreach (var c in texto)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static bool TodosIguais(string d) => d.AsSpan().TrimStart(d[0]).IsEmpty;

    private static bool CpfValido(string d)
    {
        if (TodosIguais(d))
            return false;

        return DigitoCpf(d, 9) == d[9] - '0' && DigitoCpf(d, 10) == d[10] - '0';
    }

    private static int DigitoCpf(string d, int quantidade)
    {
        var soma = 0;
        for (var i = 0; i < quantidade; i++)
            soma += (d[i] - '0') * (quantidade + 1 - i);

        var dv = soma * 10 % 11;
        return dv == 10 ? 0 : dv;
    }

    private static readonly int[] PesosCnpj1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
    private static readonly int[] PesosCnpj2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];

    private static bool CnpjValido(string d)
    {
        if (TodosIguais(d))
            return false;

        return DigitoCnpj(d, PesosCnpj1) == d[12] - '0' && DigitoCnpj(d, PesosCnpj2) == d[13] - '0';
    }

    private static int DigitoCnpj(string d, int[] pesos)
    {
        var soma = 0;
        for (var i = 0; i < pesos.Length; i++)
            soma += (d[i] - '0') * pesos[i];

        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }
}

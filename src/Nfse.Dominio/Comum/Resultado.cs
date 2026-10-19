namespace Nfse.Dominio.Comum;

/// <summary>Resultado de uma operação de domínio: um valor ou um erro de domínio, nunca exceção para regra de negócio.</summary>
public readonly record struct Resultado<T>
{
    private readonly T? _valor;

    internal Resultado(T? valor, ErroDeDominio? erro)
    {
        _valor = valor;
        Erro = erro;
    }

    public ErroDeDominio? Erro { get; }

    public bool Sucesso => Erro is null;

    public T Valor => Sucesso
        ? _valor!
        : throw new InvalidOperationException($"Resultado com erro: {Erro}.");

    public static implicit operator Resultado<T>(T valor) => new(valor, null);

    public static implicit operator Resultado<T>(ErroDeDominio erro) => new(default, erro);
}

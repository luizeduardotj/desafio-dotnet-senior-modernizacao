namespace Nfse.Fachada.Sombra;

/// <summary>
/// Tee do corpo da resposta ao cliente: cada bloco vai primeiro ao cliente e depois é copiado em memória, até
/// <paramref name="limite"/> bytes. Acima disso a cópia é abandonada (<see cref="Excedeu"/>) e o cliente segue
/// recebendo tudo, sem espera nem limite novo.
/// </summary>
internal sealed class CapturaDaResposta(Stream cliente, int limite) : Stream
{
    private MemoryStream? _copia = new();

    public bool Excedeu { get; private set; }

    /// <summary>Cópia do que foi enviado ao cliente; <c>null</c> se passou do limite.</summary>
    public byte[]? Bytes => _copia?.ToArray();

    /// <summary>Instala a captura no corpo da resposta; <see cref="IDisposable.Dispose"/> devolve o corpo original.</summary>
    public static Instalada Instalar(HttpContext ctx, int limite)
    {
        Stream original = ctx.Response.Body;
        var captura = new CapturaDaResposta(original, limite);
        ctx.Response.Body = captura;
        return new Instalada(ctx, original, captura);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        cliente.Write(buffer, offset, count);
        Copiar(buffer.AsSpan(offset, count));
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await cliente.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        Copiar(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => cliente.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => cliente.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // O corpo do cliente pertence ao servidor; só a cópia é nossa.
        if (disposing)
        {
            _copia?.Dispose();
            _copia = null;
        }

        base.Dispose(disposing);
    }

    private void Copiar(ReadOnlySpan<byte> bloco)
    {
        if (_copia is null)
        {
            return;
        }

        if (_copia.Length + bloco.Length > limite)
        {
            Excedeu = true;
            _copia.Dispose();
            _copia = null;
            return;
        }

        _copia.Write(bloco);
    }

    /// <summary>Captura instalada; o valor padrão (sem captura) não faz nada no <c>Dispose</c>.</summary>
    internal readonly struct Instalada : IDisposable
    {
        private readonly HttpContext? _ctx;
        private readonly Stream? _original;

        public Instalada(HttpContext ctx, Stream original, CapturaDaResposta captura)
        {
            _ctx = ctx;
            _original = original;
            Captura = captura;
        }

        public CapturaDaResposta? Captura { get; }

        public void Dispose()
        {
            if (_ctx is not null)
            {
                _ctx.Response.Body = _original!;
                Captura!.Dispose();
            }
        }
    }
}

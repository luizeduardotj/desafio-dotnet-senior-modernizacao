using System.Buffers;

namespace Nfse.Fachada.Encaminhamento;

/// <summary>Bufferização limitada do corpo das operações em escopo (ADR-0008).</summary>
internal static class CorpoDaRequisicao
{
    /// <summary>
    /// Lê o corpo até <paramref name="limite"/> bytes e o substitui por uma cópia rebobinável. Acima do limite, devolve
    /// <c>null</c> e o corpo passa a ser o trecho já lido seguido do restante do original, sem mais bufferização: o
    /// legado recebe os mesmos bytes e não ganhamos um limite que ele não tem.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>?> BufferizarAsync(HttpContext ctx, int limite, CancellationToken ct)
    {
        HttpRequest requisicao = ctx.Request;
        if (requisicao.ContentLength > limite)
        {
            return null;
        }

        var copia = new MemoryStream(requisicao.ContentLength is long tamanho ? (int)tamanho : 16 * 1024);
        ctx.Response.RegisterForDispose(copia);

        byte[] bloco = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int lidos;
            while ((lidos = await requisicao.Body.ReadAsync(bloco.AsMemory(), ct).ConfigureAwait(false)) > 0)
            {
                copia.Write(bloco, 0, lidos);
                if (copia.Length > limite)
                {
                    copia.Position = 0;
                    requisicao.Body = new CorpoPrefixado(copia, requisicao.Body);
                    return null;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bloco);
        }

        copia.Position = 0;
        requisicao.Body = copia;
        return copia.GetBuffer().AsMemory(0, (int)copia.Length);
    }
}

/// <summary>Trecho já lido seguido do restante do corpo original. Só leitura, sem seek.</summary>
internal sealed class CorpoPrefixado(Stream prefixo, Stream restante) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int lidos = prefixo.Read(buffer, offset, count);
        return lidos > 0 ? lidos : restante.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int lidos = await prefixo.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return lidos > 0 ? lidos : await restante.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

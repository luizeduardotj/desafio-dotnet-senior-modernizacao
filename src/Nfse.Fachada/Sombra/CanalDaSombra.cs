using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Nfse.Fachada.Sombra;

/// <summary>Canal em memória limitado entre o caminho do cliente e os workers (ADR-0009). Nunca bloqueia quem publica.</summary>
public sealed class CanalDaSombra(IOptions<OpcoesSombra> opcoes)
{
    private readonly Channel<CopiaDaSombra> _canal = Channel.CreateBounded<CopiaDaSombra>(
        new BoundedChannelOptions(opcoes.Value.CapacidadeDoCanal)
        {
            // Com Wait, TryWrite devolve false quando cheio: a cópia é descartada e quem publica segue.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    public ChannelReader<CopiaDaSombra> Leitor => _canal.Reader;

    public bool TentarPublicar(CopiaDaSombra copia) => _canal.Writer.TryWrite(copia);
}

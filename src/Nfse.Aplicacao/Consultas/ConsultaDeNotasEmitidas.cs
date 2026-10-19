using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Comum;
using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Consultas;

/// <summary>
/// Caso de uso de leitura: notas emitidas por um prestador. Sem agregado nem regra além da validação de parâmetros
/// (CQRS do lado de leitura, ADR-0007). A autorização (quem pode ver qual prestador) é do adaptador de entrada.
/// </summary>
public sealed class ConsultaDeNotasEmitidas(IContribuintes contribuintes, ILeituraDeNotas leitura)
{
    public const int LimitePadrao = 50;
    public const int LimiteMaximo = 100;

    /// <summary>Prestador ativo com essa inscrição, ou <see langword="null"/>.</summary>
    public async Task<PrestadorLido?> PrestadorAtivoAsync(string inscricaoMunicipal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(inscricaoMunicipal))
        {
            return null;
        }

        PrestadorLido? prestador = await contribuintes.ObterPorInscricaoAsync(inscricaoMunicipal, cancellationToken).ConfigureAwait(false);
        return prestador is { Ativo: true } ? prestador : null;
    }

    /// <summary>Faixa por posição, para o contrato SOAP. A validação do pedido é de quem chama.</summary>
    public Task<IReadOnlyList<NotaFiscalLida>> ListarPorFaixaAsync(
        FiltroDeNotasEmitidas filtro, int deslocamento, int limite, CancellationToken cancellationToken) =>
        leitura.ListarEmitidasPorFaixaAsync(filtro, deslocamento, limite, cancellationToken);

    /// <summary>Página por cursor no período inclusivo (contrato REST, ADR-0010).</summary>
    public async Task<Resultado<PaginaDeNotas>> ListarAsync(
        string inscricaoMunicipal,
        PeriodoDeEmissao periodo,
        SituacaoNota? situacao,
        PosicaoNaListagem? apos,
        int limite,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(periodo);

        if (limite is < 1 or > LimiteMaximo)
        {
            return new LimiteInvalido(LimiteMaximo);
        }

        PrestadorLido? prestador = await PrestadorAtivoAsync(inscricaoMunicipal, cancellationToken).ConfigureAwait(false);
        if (prestador is null)
        {
            return new PrestadorNaoEncontrado();
        }

        var filtro = new FiltroDeNotasEmitidas(
            prestador.Id,
            periodo.Inicio.ToDateTime(TimeOnly.MinValue),
            periodo.Fim.AddDays(1).ToDateTime(TimeOnly.MinValue),
            Situacao: situacao);

        // Busca um a mais para saber se há próxima página sem contar o total.
        IReadOnlyList<NotaFiscalLida> notas = await leitura
            .ListarEmitidasAposAsync(filtro, apos, limite + 1, cancellationToken).ConfigureAwait(false);

        if (notas.Count <= limite)
        {
            return new PaginaDeNotas(notas, null);
        }

        NotaFiscalLida ultima = notas[limite - 1];
        return new PaginaDeNotas([.. notas.Take(limite)], new PosicaoNaListagem(ultima.DataEmissao, ultima.Id));
    }
}

using Nfse.Aplicacao.Consultas;
using Nfse.Aplicacao.Portas;
using Nfse.Dominio.Cancelamento;
using Nfse.Dominio.Notas;

namespace Nfse.Aplicacao.Cancelamento;

/// <summary>Estado atual lido do banco, com o <c>SYSDATE</c> da mesma leitura.</summary>
/// <param name="Situacao"><see langword="null"/> se a nota não existe para o prestador.</param>
public sealed record EstadoAtualDaNota(
    DateTime AgoraNoBanco,
    SituacaoNota? Situacao,
    Competencia? Competencia,
    DateTime? DataCancelamento,
    bool GuiaPaga,
    DateTime? GuiaAtualizadaEm);

/// <summary>O que o legado respondeu ao ERP, já lido pelo adaptador SOAP.</summary>
public enum RespostaObservada
{
    Cancelou,
    NotaNaoEncontrada,
    NotaSubstituida,
    PrazoExpirado,
    GuiaPaga,
    Fault,
}

public enum ClasseDeSombra
{
    NaoClassificada,
    Corrida,
    FronteiraRelogio,
    Indeterminado,
}

/// <param name="DecisaoLegado">A decisão que o legado tomou, reconstruída da resposta e do estado.</param>
/// <param name="DecisaoNovo">A decisão que a regra do domínio tomaria no mesmo estado e instante.</param>
public sealed record AvaliacaoDaSombra(
    bool PrestadorEncontrado,
    DecisaoDeCancelamento? DecisaoLegado,
    DecisaoDeCancelamento? DecisaoNovo,
    ClasseDeSombra? Classe)
{
    public bool Igual => Classe is null && DecisaoLegado == DecisaoNovo;

    /// <summary>
    /// O documento informado no pedido é o do cadastro do prestador da IM (C18/K04: o legado não confere)?
    /// <see langword="null"/> quando não há o que conferir: prestador não encontrado, pedido ou cadastro sem documento.
    /// Só o resultado sai daqui, nunca o documento.
    /// </summary>
    public bool? DocumentoConfere { get; init; }
}

/// <summary>
/// Sombra do cancelamento (ADR-0009 §3): avalia a decisão sem escrever, sobre o estado anterior reconstruído a partir
/// do estado atual e da resposta do legado. Avaliar sobre o estado atual seria inútil: depois do cancelamento a nota
/// está cancelada e toda avaliação daria "já cancelada".
/// </summary>
public sealed class SombraDoCancelamento(IContribuintes contribuintes, ILeituraParaSombra leitura)
{
    /// <summary>Folga para o truncamento de <c>DATE</c> em segundos e a diferença entre duas leituras de <c>SYSDATE</c>.</summary>
    public static readonly TimeSpan Tolerancia = TimeSpan.FromSeconds(2);

    /// <param name="documentoInformado">CNPJ do prestador como veio no pedido; só é conferido com o cadastro.</param>
    /// <param name="idade">Tempo entre o recebimento do pedido na fachada e o envio da cópia, medido lá.</param>
    public async Task<AvaliacaoDaSombra> AvaliarAsync(
        string inscricaoMunicipal, long numero, string? documentoInformado, RespostaObservada observada, DateTime? dataHoraObservada,
        TimeSpan idade, CancellationToken cancellationToken)
    {
        PrestadorLido? prestador = string.IsNullOrEmpty(inscricaoMunicipal)
            ? null
            : await contribuintes.ObterPorInscricaoAsync(inscricaoMunicipal, cancellationToken).ConfigureAwait(false);
        if (prestador is not { Ativo: true })
        {
            return new AvaliacaoDaSombra(false, null, null, null);
        }

        // O cadastro já foi lido para achar o prestador: conferir o documento não custa outra leitura.
        AvaliacaoDaSombra avaliacao = await AvaliarComPrestadorAsync(prestador, numero, observada, dataHoraObservada, idade, cancellationToken)
            .ConfigureAwait(false);
        return avaliacao with { DocumentoConfere = Conferir(documentoInformado, prestador.Documento) };
    }

    /// <summary>Iguais só nos dígitos e sem zeros à esquerda: um ERP que omite o zero inicial não conta como divergente.</summary>
    public static bool? Conferir(string? informado, string? cadastrado)
    {
        string a = SoDigitosSemZerosAEsquerda(informado);
        string b = SoDigitosSemZerosAEsquerda(cadastrado);
        return a.Length == 0 || b.Length == 0 ? null : a == b;
    }

    private static string SoDigitosSemZerosAEsquerda(string? documento) =>
        string.Concat((documento ?? "").Where(char.IsAsciiDigit)).TrimStart('0');

    private async Task<AvaliacaoDaSombra> AvaliarComPrestadorAsync(
        PrestadorLido prestador, long numero, RespostaObservada observada, DateTime? dataHoraObservada, TimeSpan idade,
        CancellationToken cancellationToken)
    {
        if (observada == RespostaObservada.Fault)
        {
            // K09/K11: o legado pode ter comitado antes de falhar; não há como saber o estado anterior.
            return new AvaliacaoDaSombra(true, null, null, ClasseDeSombra.Indeterminado);
        }

        EstadoAtualDaNota atual = await leitura.LerAsync(prestador.Id, numero, cancellationToken).ConfigureAwait(false);
        DateTime t0 = Truncar(atual.AgoraNoBanco - idade);

        EstadoParaCancelamento? antes;
        DecisaoDeCancelamento decisaoLegado;
        if (atual.Situacao is not { } situacao || atual.Competencia is not { } competencia)
        {
            antes = null;
            decisaoLegado = Decisao(observada);
        }
        else if (observada == RespostaObservada.Cancelou)
        {
            if (situacao != SituacaoNota.Cancelada || dataHoraObservada is not { } dataHora)
            {
                return new AvaliacaoDaSombra(true, null, null, ClasseDeSombra.Indeterminado);
            }

            // O legado acabou de cancelar se o DT_CANCELAMENTO é deste pedido; senão a nota já estava cancelada (K01).
            bool cancelouAgora = dataHora >= t0 - Tolerancia;
            antes = new EstadoParaCancelamento(cancelouAgora ? SituacaoNota.Normal : SituacaoNota.Cancelada, competencia, atual.GuiaPaga);
            decisaoLegado = cancelouAgora ? DecisaoDeCancelamento.Permitido : DecisaoDeCancelamento.JaCancelada;
        }
        else
        {
            // E050–E053 não mudam o estado: o anterior é o atual.
            antes = new EstadoParaCancelamento(situacao, competencia, atual.GuiaPaga);
            decisaoLegado = Decisao(observada);
        }

        DecisaoDeCancelamento decisaoNovo = PoliticaDeCancelamento.Avaliar(antes, t0);
        if (decisaoNovo == decisaoLegado)
        {
            return new AvaliacaoDaSombra(true, decisaoLegado, decisaoNovo, null);
        }

        return new AvaliacaoDaSombra(true, decisaoLegado, decisaoNovo, Classificar(antes, atual, t0));
    }

    private static ClasseDeSombra Classificar(EstadoParaCancelamento? antes, EstadoAtualDaNota atual, DateTime t0)
    {
        if (antes is not null && (antes.Competencia.FimDoPrazoDeCancelamento - t0).Duration() <= Tolerancia)
        {
            return ClasseDeSombra.FronteiraRelogio;
        }

        // Guia paga entre a resposta do legado e esta leitura: a reconstrução vê uma guia que o legado não viu.
        if (atual.GuiaPaga && atual.GuiaAtualizadaEm > t0)
        {
            return ClasseDeSombra.Corrida;
        }

        return ClasseDeSombra.NaoClassificada;
    }

    private static DecisaoDeCancelamento Decisao(RespostaObservada observada) => observada switch
    {
        RespostaObservada.NotaNaoEncontrada => DecisaoDeCancelamento.NotaNaoEncontrada,
        RespostaObservada.NotaSubstituida => DecisaoDeCancelamento.NotaSubstituida,
        RespostaObservada.PrazoExpirado => DecisaoDeCancelamento.PrazoExpirado,
        RespostaObservada.GuiaPaga => DecisaoDeCancelamento.GuiaPaga,
        _ => DecisaoDeCancelamento.Permitido,
    };

    private static DateTime Truncar(DateTime instante) => instante.AddTicks(-(instante.Ticks % TimeSpan.TicksPerSecond));
}

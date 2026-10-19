using System.Diagnostics;

namespace Nfse.Fachada.Sombra;

/// <summary>
/// O que a sombra precisa, fora do caminho do cliente, para repetir a leitura no novo e comparar, ou para o novo
/// avaliar o cancelamento sem escrever (ADR-0009, seção 3).
/// </summary>
/// <param name="Operacao">Operação em escopo.</param>
/// <param name="SoapAction">Header como o cliente mandou, reenviado ao novo.</param>
/// <param name="ContentType">Content-Type da requisição original.</param>
/// <param name="Requisicao">Corpo bufferizado da requisição.</param>
/// <param name="StatusLegado">Status HTTP que o cliente recebeu.</param>
/// <param name="RespostaLegado">Corpo que o cliente recebeu.</param>
/// <param name="T0">Recebimento da requisição (<see cref="Stopwatch.GetTimestamp"/>), base de <c>sombra_atraso</c> e da
/// <c>idadeMs</c> do cancelamento.</param>
/// <param name="Origem">Contexto do trace da requisição original, para o span link.</param>
public sealed record CopiaDaSombra(
    string Operacao,
    string SoapAction,
    string? ContentType,
    byte[] Requisicao,
    int StatusLegado,
    byte[] RespostaLegado,
    long T0,
    ActivityContext Origem);

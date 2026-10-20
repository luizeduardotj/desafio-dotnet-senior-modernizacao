using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nfse.Fachada.Sombra.Reproducao;

/// <summary>
/// Arquivo de reprodução, como fica no disco. Os metadados ficam em claro e não têm dado pessoal: operação, trace,
/// instante, chave. Eles também entram como dado associado do AES-GCM, então trocar um deles invalida a decifração. O
/// conteúdo só existe cifrado.
/// </summary>
public sealed record RegistroDeReproducao(
    int Versao,
    string Id,
    string Operacao,
    string TraceId,
    long CriadoEmUnix,
    string ChaveId,
    byte[] Nonce,
    byte[] Tag,
    byte[] Cifrado);

/// <summary>
/// O que a reprodução precisa, e só isso (minimização, LGPD). A <see cref="Resposta"/> de leitura (uma página de notas
/// com documentos de tomadores) não é guardada: o SHA-256 basta para saber se o lado que respondeu ainda devolve o
/// mesmo. No cancelamento ela é pequena e o novo precisa dela para reavaliar a decisão (ADR-0009, seção 3).
/// </summary>
/// <param name="Status">Status que o cliente recebeu.</param>
/// <param name="Sha256Resposta">SHA-256 do corpo que o cliente recebeu, o mesmo do log da divergência.</param>
/// <param name="Resposta">Corpo que o cliente recebeu; só em <c>CancelarNfse</c>.</param>
/// <param name="Invertida">O novo respondeu ao cliente (sombra invertida).</param>
public sealed record ConteudoDeReproducao(
    string SoapAction,
    string? ContentType,
    byte[] Requisicao,
    int Status,
    string Sha256Resposta,
    byte[]? Resposta,
    bool Invertida);

/// <summary>AES-256-GCM do <see cref="ConteudoDeReproducao"/> (ADR-0009, seção 4; ADR-0012).</summary>
public static class EnvelopeCifrado
{
    public const int VersaoAtual = 1;

    private const int TamanhoDoNonce = 12;
    private const int TamanhoDaTag = 16;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static RegistroDeReproducao Cifrar(
        ConteudoDeReproducao conteudo, string id, string operacao, string traceId, DateTimeOffset criadoEm, string chaveId, byte[] chave)
    {
        ArgumentNullException.ThrowIfNull(conteudo);
        byte[] claro = JsonSerializer.SerializeToUtf8Bytes(conteudo, Json);
        byte[] nonce = RandomNumberGenerator.GetBytes(TamanhoDoNonce);
        byte[] cifrado = new byte[claro.Length];
        byte[] tag = new byte[TamanhoDaTag];
        var registro = new RegistroDeReproducao(VersaoAtual, id, operacao, traceId, criadoEm.ToUnixTimeSeconds(), chaveId, nonce, tag, cifrado);

        try
        {
            using var aes = new AesGcm(chave, TamanhoDaTag);
            aes.Encrypt(nonce, claro, cifrado, tag, DadoAssociado(registro));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
        }

        return registro;
    }

    /// <param name="chaves">Chaves por identificador (a atual e as anteriores ainda na retenção).</param>
    /// <exception cref="CryptographicException">Sem a chave do registro, chave errada ou registro alterado.</exception>
    public static ConteudoDeReproducao Decifrar(RegistroDeReproducao registro, IReadOnlyDictionary<string, byte[]> chaves)
    {
        ArgumentNullException.ThrowIfNull(registro);
        ArgumentNullException.ThrowIfNull(chaves);
        if (registro.Versao != VersaoAtual)
        {
            throw new CryptographicException($"versão {registro.Versao} desconhecida");
        }

        if (!chaves.TryGetValue(registro.ChaveId, out byte[]? chave))
        {
            throw new CryptographicException($"sem a chave {registro.ChaveId}");
        }

        byte[] claro = new byte[registro.Cifrado.Length];
        try
        {
            using var aes = new AesGcm(chave, TamanhoDaTag);
            aes.Decrypt(registro.Nonce, registro.Cifrado, registro.Tag, claro, DadoAssociado(registro));
            return JsonSerializer.Deserialize<ConteudoDeReproducao>(claro, Json)
                ?? throw new CryptographicException("conteúdo vazio");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
        }
    }

    private static byte[] DadoAssociado(RegistroDeReproducao r) => Encoding.UTF8.GetBytes(string.Join(
        '\n', "nfse-sombra-reproducao", r.Versao.ToString(CultureInfo.InvariantCulture), r.Id, r.Operacao, r.TraceId,
        r.CriadoEmUnix.ToString(CultureInfo.InvariantCulture), r.ChaveId));
}

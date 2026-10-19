using System.IO.Hashing;
using System.Text;

namespace Nfse.Fachada.Roteamento;

/// <summary>
/// Balde 0..99 determinístico entre processos e réplicas: <c>xxHash64(operação + "|" + chave) % 100</c> (ADR-0008).
/// Nunca <c>string.GetHashCode</c> (aleatório por processo) nem o HMAC de pseudonimização (chave rotacionada).
/// </summary>
public static class HashDeRoteamento
{
    public static ulong Hash(string operacao, string chave) =>
        XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(operacao + "|" + chave));

    public static int Balde(string operacao, string chave) => (int)(Hash(operacao, chave) % 100);
}

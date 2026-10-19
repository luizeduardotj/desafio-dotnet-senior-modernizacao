using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nfse.Aplicacao.Consultas;

namespace Nfse.Adaptadores.Rest;

/// <summary>
/// Cursor opaco da paginação por keyset: posição <c>(DT_EMISSAO, ID)</c> mais um resumo dos filtros, para recusar
/// cursor reaproveitado com outra consulta (ADR-0010).
/// </summary>
internal static class Cursor
{
    public static string Codificar(PosicaoNaListagem posicao, string filtros) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            $"v1|{posicao.DataEmissao.Ticks.ToString(CultureInfo.InvariantCulture)}|{posicao.Id.ToString(CultureInfo.InvariantCulture)}|{Resumo(filtros)}"));

    public static bool TentarDecodificar(string? texto, string filtros, out PosicaoNaListagem? posicao)
    {
        posicao = null;
        if (string.IsNullOrEmpty(texto))
        {
            return true;
        }

        try
        {
            string[] partes = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(texto)).Split('|');
            if (partes is not ["v1", var ticks, var id, var resumo] || resumo != Resumo(filtros))
            {
                return false;
            }

            posicao = new PosicaoNaListagem(
                new DateTime(long.Parse(ticks, CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
                long.Parse(id, CultureInfo.InvariantCulture));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string Resumo(string filtros) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(filtros)).AsSpan(0, 8));
}

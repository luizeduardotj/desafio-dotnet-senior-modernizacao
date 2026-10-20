using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Nfse.Arquitetura.Testes;

/// <summary>
/// ADR-0003 e ADR-0011: prazo e "agora" vêm do <c>SYSDATE</c> do banco, congelável com <c>FIXED_DATE</c>. Nenhum
/// assembly de <c>src/Nfse.*</c> pode ler o relógio da máquina (<c>DateTime.Now</c>/<c>UtcNow</c>/<c>Today</c>,
/// <c>DateTimeOffset.Now</c>/<c>UtcNow</c>) nem mandar ao Oracle um relógio que o <c>FIXED_DATE</c> não congela
/// (<c>SYSTIMESTAMP</c>, <c>CURRENT_DATE</c>...). O NetArchTest só vê dependências de tipo; aqui o IL de cada método é
/// lido com <c>System.Reflection.Metadata</c>, que vem no próprio runtime, sem pacote a mais.
/// </summary>
public class RelogioDoBancoTests
{
    private static readonly HashSet<string> GettersProibidos = new(StringComparer.Ordinal)
    {
        "System.DateTime::get_Now",
        "System.DateTime::get_UtcNow",
        "System.DateTime::get_Today",
        "System.DateTimeOffset::get_Now",
        "System.DateTimeOffset::get_UtcNow",
    };

    // Funções do Oracle que o FIXED_DATE não congela (ADR-0011, "Riscos"); só SYSDATE é aceito no SQL do serviço novo.
    private static readonly Regex RelogioNoSql =
        new(@"\b(SYSTIMESTAMP|CURRENT_DATE|CURRENT_TIMESTAMP|LOCALTIMESTAMP)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Usos legítimos, um por método (<c>Assembly:Tipo::Método</c>), cada um com o motivo. Vazia hoje: medir duração usa
    /// <c>Stopwatch</c>, que não é relógio de parede. Quem precisar de horário fora do banco (ex.: <c>TimeProvider</c>
    /// num limitador de taxa) entra aqui com a justificativa, e o teste abaixo cobra que a exceção continue usada.
    /// </summary>
    private static readonly Dictionary<string, string> Excecoes = new(StringComparer.Ordinal);

    [Fact]
    public void Servico_novo_e_fachada_nao_leem_o_relogio_da_maquina()
    {
        List<string> achados = AssembliesDoSrc().SelectMany(Varrer).ToList();

        string[] proibidos = achados.Where(a => !Excecoes.ContainsKey(Local(a))).ToArray();
        Assert.True(proibidos.Length == 0,
            "relógio fora do SYSDATE do banco (ADR-0003); use o horário devolvido pelo Oracle ou justifique em Excecoes:\n  " +
            string.Join("\n  ", proibidos));

        string[] obsoletas = Excecoes.Keys.Where(e => !achados.Any(a => Local(a) == e)).ToArray();
        Assert.True(obsoletas.Length == 0, "exceção sem uso, remova: " + string.Join(", ", obsoletas));
    }

    [Fact]
    public void Todo_projeto_de_src_e_varrido()
    {
        string src = Path.Combine(RaizDoRepositorio(), "src");
        string[] projetos = Directory.GetDirectories(src, "Nfse.*").Select(Path.GetFileName).OfType<string>().ToArray();
        string[] varridos = AssembliesDoSrc().Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();

        Assert.NotEmpty(projetos);
        Assert.Empty(projetos.Except(varridos, StringComparer.Ordinal));
    }

    [Fact]
    public void Detector_acha_o_relogio_no_proprio_assembly_de_teste()
    {
        string[] achados = Varrer(typeof(RelogioDoBancoTests).Assembly.Location).ToArray();

        Assert.Contains(achados, a => a.Contains(nameof(Sentinela.LeRelogio), StringComparison.Ordinal) && a.Contains("DateTime::get_UtcNow", StringComparison.Ordinal));
        Assert.Contains(achados, a => a.Contains(nameof(Sentinela.LeRelogioNumaLambda), StringComparison.Ordinal) && a.Contains("DateTimeOffset::get_Now", StringComparison.Ordinal));
        Assert.Contains(achados, a => a.Contains(nameof(Sentinela.SqlComSystimestamp), StringComparison.Ordinal) && a.Contains("SYSTIMESTAMP", StringComparison.Ordinal));
    }

    private static IEnumerable<string> AssembliesDoSrc() =>
        Directory.GetFiles(AppContext.BaseDirectory, "Nfse.*.dll")
            .Where(c => !Path.GetFileName(c).Contains(".Testes", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string Local(string achado) => achado[..achado.IndexOf(" -> ", StringComparison.Ordinal)];

    /// <summary>Cada achado é <c>Assembly:Tipo::Método -> o que foi lido</c>.</summary>
    private static List<string> Varrer(string caminho)
    {
        using var arquivo = File.OpenRead(caminho);
        using var pe = new PEReader(arquivo);
        MetadataReader md = pe.GetMetadataReader();
        string assembly = md.GetString(md.GetAssemblyDefinition().Name);
        var achados = new List<string>();

        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition tipo = md.GetTypeDefinition(th);
            foreach (MethodDefinitionHandle mh in tipo.GetMethods())
            {
                MethodDefinition metodo = md.GetMethodDefinition(mh);
                if (metodo.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                string local = $"{assembly}:{NomeDoTipo(md, th)}::{md.GetString(metodo.Name)}";
                BlobReader il = pe.GetMethodBody(metodo.RelativeVirtualAddress).GetILReader();
                foreach (string lido in LeiturasDeRelogio(md, il))
                {
                    achados.Add($"{local} -> {lido}");
                }
            }
        }

        return achados;
    }

    private static List<string> LeiturasDeRelogio(MetadataReader md, BlobReader il)
    {
        var lidos = new List<string>();
        while (il.RemainingBytes > 0)
        {
            OpCode op = LerOpCode(ref il);
            switch (op.OperandType)
            {
                case OperandType.InlineMethod:
                    string? membro = NomeDoMembro(md, MetadataTokens.EntityHandle(il.ReadInt32()));
                    if (membro is not null && GettersProibidos.Contains(membro))
                    {
                        lidos.Add(membro);
                    }

                    break;
                case OperandType.InlineString:
                    string texto = md.GetUserString(MetadataTokens.UserStringHandle(il.ReadInt32()));
                    Match m = RelogioNoSql.Match(texto);
                    if (m.Success)
                    {
                        lidos.Add($"\"{m.Value}\" em literal");
                    }

                    break;
                case OperandType.InlineSwitch:
                    int alvos = il.ReadInt32(); // lido antes: `il.Offset += 4 * il.ReadInt32()` somaria ao offset antigo
                    il.Offset += 4 * alvos;
                    break;
                default:
                    il.Offset += TamanhoDoOperando(op.OperandType);
                    break;
            }
        }

        return lidos;
    }

    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static OpCode LerOpCode(ref BlobReader il)
    {
        byte b = il.ReadByte();
        short valor = b == 0xFE ? (short)(0xFE00 | il.ReadByte()) : b;
        return OpCodes[valor];
    }

    private static int TamanhoDoOperando(OperandType tipo) => tipo switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        _ => 4,
    };

    private static string? NomeDoMembro(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MemberReference:
                MemberReference mr = md.GetMemberReference((MemberReferenceHandle)h);
                return mr.Parent.Kind == HandleKind.TypeReference
                    ? $"{NomeDaReferencia(md, (TypeReferenceHandle)mr.Parent)}::{md.GetString(mr.Name)}"
                    : null;
            case HandleKind.MethodSpecification:
                return NomeDoMembro(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            default:
                return null;
        }
    }

    private static string NomeDaReferencia(MetadataReader md, TypeReferenceHandle h)
    {
        TypeReference t = md.GetTypeReference(h);
        return $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}";
    }

    private static string NomeDoTipo(MetadataReader md, TypeDefinitionHandle h)
    {
        TypeDefinition t = md.GetTypeDefinition(h);
        TypeDefinitionHandle externo = t.GetDeclaringType();
        return externo.IsNil
            ? $"{md.GetString(t.Namespace)}.{md.GetString(t.Name)}"
            : $"{NomeDoTipo(md, externo)}/{md.GetString(t.Name)}";
    }

    private static string RaizDoRepositorio()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Nfse.Moderno.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}

/// <summary>Controle positivo do detector: cada forma que ele precisa achar, inclusive dentro de lambda.</summary>
internal static class Sentinela
{
    public static DateTime LeRelogio() => DateTime.UtcNow;

    public static Func<DateTimeOffset> LeRelogioNumaLambda() => () => DateTimeOffset.Now;

    public static string SqlComSystimestamp() => "SELECT SYSTIMESTAMP FROM DUAL";
}

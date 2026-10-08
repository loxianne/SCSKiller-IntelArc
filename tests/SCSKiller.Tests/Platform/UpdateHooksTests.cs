using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SCSKiller.Tests.Platform;

/// <summary>Velopack logs from inside VelopackApp.Run, before its locator is set, and an UpdateManager built without that
/// locator throws. Read from the built app's IL (the tests don't reference the WinUI project): the hooks and the logger never
/// touch Updater, and Updater's static initializer builds no UpdateManager, so a type initializer that failed during Run
/// can't take every later use of Updater down with it.</summary>
public class UpdateHooksTests
{
    /// <summary>The app this test build's configuration built (SCSKiller.App.csproj's x64 output). Missing: a failure on CI,
    /// which builds the whole solution first, and null locally, where only the tests may have been built.</summary>
    static string? AppDll()
    {
        var config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;   // tests\SCSKiller.Tests\bin\<config>\<tfm>\
        var path = Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "bin", "x64", config, "net10.0-windows10.0.26100.0", "win-x64", "SCSKiller.dll");
        if (File.Exists(path)) return path;
        Assert.True(Environment.GetEnvironmentVariable("CI") == null, $"the app isn't built at {path}");
        return null;
    }

    [Fact]
    public void The_hooks_and_the_update_log_never_touch_Updater_and_its_static_initializer_builds_no_UpdateManager()
    {
        if (AppDll() is not { } path) return;
        using var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        var updater = Type(md, "Updater");
        var hooks = Type(md, "UpdateHooks");

        var touched = Within(md, hooks).SelectMany(m => Referenced(pe, md, m)).Where(r => r.Type == updater || IsNestedIn(md, r.Type, updater))
            .Select(r => r.Name).ToList();
        Assert.True(touched.Count == 0, "UpdateHooks reaches Updater: " + string.Join(", ", touched));

        var cctor = md.GetTypeDefinition(updater).GetMethods().Single(h => md.GetString(md.GetMethodDefinition(h).Name) == ".cctor");
        var built = Referenced(pe, md, cctor).Where(r => r.Name is "Manager" or "UpdateManager" or "Velo" || r.TypeName is "UpdateManager" or "Velo").Select(r => r.Name).ToList();
        Assert.True(built.Count == 0, "Updater's static initializer builds an UpdateManager: " + string.Join(", ", built));
    }

    static TypeDefinitionHandle Type(MetadataReader md, string name) =>
        md.TypeDefinitions.Single(h => md.GetTypeDefinition(h) is var t && md.GetString(t.Name) == name && md.GetString(t.Namespace) == "SCSKiller.App");

    static bool IsNestedIn(MetadataReader md, TypeDefinitionHandle type, TypeDefinitionHandle outer)
    {
        for (var t = type; !t.IsNil; t = md.GetTypeDefinition(t).GetDeclaringType())
            if (t == outer) return true;
        return false;
    }

    /// <summary>The methods of <paramref name="type"/> and of its nested types (lambdas' closures among them).</summary>
    static IEnumerable<MethodDefinitionHandle> Within(MetadataReader md, TypeDefinitionHandle type) =>
        md.GetTypeDefinition(type).GetMethods().Concat(md.GetTypeDefinition(type).GetNestedTypes().SelectMany(n => Within(md, n)));

    static readonly Dictionary<short, OperandType> Operands = typeof(OpCodes).GetFields().Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value, o => o.OperandType);

    /// <summary>Every field and method the body names: its declaring type (nil for another assembly's), its type's name, its name.</summary>
    static IEnumerable<(TypeDefinitionHandle Type, string TypeName, string Name)> Referenced(PEReader pe, MetadataReader md, MethodDefinitionHandle method)
    {
        var rva = md.GetMethodDefinition(method).RelativeVirtualAddress;
        if (rva == 0) yield break;
        var il = pe.GetMethodBody(rva).GetILReader();
        while (il.RemainingBytes > 0)
        {
            short code = il.ReadByte();
            if (code == 0xFE) code = unchecked((short)(0xFE00 | il.ReadByte()));
            switch (Operands[code])
            {
                case OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineTok:
                    if (Member(md, MetadataTokens.EntityHandle(il.ReadInt32())) is { } m) yield return m;
                    break;
                case OperandType.InlineSwitch: il.Offset += 4 * il.ReadInt32(); break;
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar: il.Offset += 1; break;
                case OperandType.InlineVar: il.Offset += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: il.Offset += 8; break;
                default: il.Offset += 4; break;
            }
        }
    }

    static (TypeDefinitionHandle, string, string)? Member(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
                var m = md.GetMethodDefinition((MethodDefinitionHandle)h);
                return (m.GetDeclaringType(), md.GetString(md.GetTypeDefinition(m.GetDeclaringType()).Name), md.GetString(m.Name));
            case HandleKind.FieldDefinition:
                var f = md.GetFieldDefinition((FieldDefinitionHandle)h);
                return (f.GetDeclaringType(), md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name), md.GetString(f.Name));
            case HandleKind.MemberReference:
                var r = md.GetMemberReference((MemberReferenceHandle)h);
                var parent = r.Parent.Kind switch
                {
                    HandleKind.TypeReference => md.GetString(md.GetTypeReference((TypeReferenceHandle)r.Parent).Name),
                    HandleKind.TypeDefinition => md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)r.Parent).Name),
                    _ => "",
                };
                return (r.Parent.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)r.Parent : default, parent, md.GetString(r.Name));
            case HandleKind.MethodSpecification:
                return Member(md, md.GetMethodSpecification((MethodSpecificationHandle)h).Method);
            default:
                return null;
        }
    }
}

using System.Text.Json;
using Mono.Cecil;

namespace Weaver;

// Build-time half of the iOS mod support (the runtime half is ModRuntime).
//   prepare --bepinex DIR --project DIR --runtime IOSPort.ModRuntime.dll --vendor DIR --weaver Weaver.dll
//   weave   --project DIR --dll FILE [--source FILE]      (patchers + hook points, in place)
//   clean   --project DIR
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: Weaver prepare|weave|clean --option value ...");
            return 2;
        }
        var options = new Options(args.Skip(1).ToArray());
        try
        {
            return args[0] switch
            {
                "prepare" => Prepare.Run(options),
                "weave" => Weave(options),
                "clean" => Clean(options),
                _ => throw new ArgumentException("unknown command " + args[0]),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e);
            return 1;
        }
    }

    static int Clean(Options o)
    {
        Prepare.Clean(o.Require("project"));
        return 0;
    }

    static int Weave(Options o)
    {
        string project = o.Require("project");
        string dll = o.Require("dll");
        string source = o.Get("source") ?? dll;
        string state = Path.Combine(project, "IOSPortMods");
        var config = JsonSerializer.Deserialize<WeaverConfig>(File.ReadAllText(Path.Combine(state, "weaver.json")))!;

        var resolver = new Resolver();
        foreach (var dir in new[]
        {
            Path.GetDirectoryName(Path.GetFullPath(dll))!,
            Path.Combine(project, "Library", "Bee", "PlayerScriptAssemblies"),
            Path.Combine(project, "Library", "ScriptAssemblies"),
            Path.Combine(project, "Assets", "Plugins"),
            Path.Combine(project, "Assets", "IOSPortMods", "Plugins"),
            config.Vendor,
            Prepare.UnityManaged(),
            Prepare.UnityAot(),
        }.Distinct().Where(Directory.Exists))
            resolver.AddSearchDirectory(dir);

        bool symbols = File.Exists(Path.ChangeExtension(source, ".pdb"));
        var assembly = AssemblyDefinition.ReadAssembly(source, new ReaderParameters
        {
            AssemblyResolver = resolver,
            InMemory = true,
            ReadSymbols = symbols,
        });
        string name = assembly.Name.Name;
        string fileName = name + ".dll";

        Patchers.Init(Path.Combine(config.Bepinex, "patchers"), Path.Combine(config.Bepinex, "plugins"), Path.Combine(state, "patcher-work"), Path.Combine(project, "Assets", "Plugins"));
        if (Patchers.Targets(fileName))
        {
            Patchers.Apply(assembly, fileName);
            // References the patchers imported only land in the module's reference tables once written,
            // so round-trip before retargeting them.
            var stream = new MemoryStream();
            assembly.Write(stream, new WriterParameters { WriteSymbols = false });
            stream.Position = 0;
            assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { AssemblyResolver = resolver, InMemory = true });
            symbols = false;
            int retargeted = Rewrites.RetargetCoreLib(assembly.MainModule);
            if (retargeted > 0)
                Console.WriteLine($"[iosport] {fileName}: pointed {retargeted} .NET type reference(s) from the patchers at mscorlib");
        }

        // id, assembly, type, method, reason, kind
        var entries = File.ReadAllLines(Path.Combine(state, "hooks.tsv"))
            .Select(l => l.Split('\t'))
            .Where(p => p.Length >= 6)
            .Select(p => new HookWeaver.Entry(int.Parse(p[0]), Enum.Parse<HookWeaver.Kind>(p[5]), p[1], p[2], p[3]))
            .ToList();
        var warnings = new List<string>();
        int woven = 0;
        if (config.Assemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var runtime = AssemblyDefinition.ReadAssembly(config.Runtime, new ReaderParameters { AssemblyResolver = resolver });
            woven = HookWeaver.Weave(assembly, runtime.MainModule, entries, warnings);
        }
        Patchers.Finish();

        string temp = dll + ".iosport-tmp";
        assembly.Write(temp, new WriterParameters { WriteSymbols = symbols });
        File.Move(temp, dll, true);
        if (symbols)
            File.Move(Path.ChangeExtension(temp, ".pdb"), Path.ChangeExtension(dll, ".pdb"), true);

        Console.WriteLine($"[iosport] {fileName}: {woven} hook points" + (Patchers.Targets(fileName) ? ", patchers applied" : ""));
        foreach (var w in warnings)
            Console.WriteLine("[iosport]   ! " + w);
        return 0;
    }
}

sealed class Options
{
    readonly Dictionary<string, string> values = new();

    public Options(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i += 2)
            values[args[i].TrimStart('-')] = args[i + 1];
    }

    public string? Get(string key) => values.TryGetValue(key, out var v) ? v : null;
    public string Require(string key) => Get(key) ?? throw new ArgumentException("missing --" + key);
}

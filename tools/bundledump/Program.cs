using AssetsTools.NET;
using AssetsTools.NET.Extra;

// Lists every asset path in each bundle's AssetBundle.m_Container as "bundle file \t asset path".
var am = new AssetsManager();
using var outFile = new StreamWriter(args[1]);
int ok = 0, failed = 0;
foreach (var path in Directory.GetFiles(args[0], "*.bundle", SearchOption.AllDirectories))
{
    try
    {
        var bun = am.LoadBundleFile(path, true);
        var inst = am.LoadAssetsFileFromBundle(bun, 0, false);
        foreach (var info in inst.file.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var bf = am.GetBaseField(inst, info);
            foreach (var pair in bf["m_Container.Array"].Children)
                outFile.WriteLine($"{Path.GetRelativePath(args[0], path)}\t{pair["first"].AsString}");
        }
        ok++;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"{Path.GetFileName(path)}: {e.Message}");
        failed++;
    }
    am.UnloadAll();
}
Console.WriteLine($"bundles read: {ok}, failed: {failed}");

using System.Text.Json;
using AddressablesTools;

// Dumps every catalog key with its location: internal id, provider, type and owning bundle.
var ccd = AddressablesCatalogFileParser.FromBinaryData(File.ReadAllBytes(args[0]));
using var outFile = new StreamWriter(args[1]);
foreach (var key in ccd.Resources.Keys)
{
    foreach (var loc in ccd.Resources[key])
    {
        string? bundle = null;
        var deps = loc.Dependencies;
        if (deps == null && loc.DependencyKey != null && ccd.Resources.TryGetValue(loc.DependencyKey, out var d))
            deps = d;
        if (deps != null && deps.Count > 0)
            bundle = deps[0].InternalId;
        outFile.WriteLine(JsonSerializer.Serialize(new
        {
            key = key.ToString(),
            keyType = key.GetType().Name,
            id = loc.InternalId,
            provider = loc.ProviderId,
            type = loc.Type?.ClassName,
            bundle,
        }));
    }
}

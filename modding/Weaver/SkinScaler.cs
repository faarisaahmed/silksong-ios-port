using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Weaver;

// The iPhone build stores the biggest tk2d sprite atlases at half resolution to save memory. Skin mods
// (Stitchwork packs) ship per-sprite PNGs at PC resolution in .../Sprites/<Collection>/<atlasN>/<sprite>.png;
// Stitchwork treats sprites bigger than the atlas slot as "expanded" art and grows the atlas until it no longer
// fits on the GPU. Scaling those PNGs by the same factor as their atlas keeps them a drop-in fit.
static class SkinScaler
{
    public static int ScaleToBuild(string pluginsDir, string project)
    {
        var factors = AtlasFactors(project);
        if (factors.Count == 0 || !Directory.Exists(pluginsDir))
            return 0;

        var jobs = new List<(string file, double factor)>();
        foreach (var file in Directory.GetFiles(pluginsDir, "*.png", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(pluginsDir, file).Split(Path.DirectorySeparatorChar);
            if (parts.Length < 3)
                continue;
            string key = parts[^3] + "/" + parts[^2]; // <Collection>/<atlasN>
            if (factors.TryGetValue(key, out double f))
                jobs.Add((file, f));
        }

        int done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, job =>
        {
            var (w, h) = PngSize(job.file);
            if (w <= 0 || h <= 0)
                return;
            // Round down so the image never ends up a pixel larger than its slot.
            int nw = Math.Max(1, (int)Math.Floor(w * job.factor));
            int nh = Math.Max(1, (int)Math.Floor(h * job.factor));
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/sips", $"-z {nh} {nw} \"{job.file}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit();
            if (p.ExitCode == 0)
                Interlocked.Increment(ref done);
        });
        return done;
    }

    // "<Collection>/<atlasN>" -> iPhone size / original size, for atlases the iPhone build shrinks.
    static Dictionary<string, double> AtlasFactors(string project)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string collections = Path.Combine(project, "Assets", "Collections");
        if (!Directory.Exists(collections))
            return result;
        foreach (var dir in Directory.GetDirectories(collections, "* Data"))
        {
            string collection = Path.GetFileName(dir)[..^" Data".Length];
            foreach (var png in Directory.GetFiles(dir, "*.png"))
            {
                string metaPath = png + ".meta";
                if (!File.Exists(metaPath))
                    continue;
                string meta = File.ReadAllText(metaPath);
                int ios = meta.IndexOf("buildTarget: iOS", StringComparison.Ordinal);
                if (ios < 0)
                    continue;
                var max = Regex.Match(meta[ios..], @"maxTextureSize: (\d+)");
                var (w, h) = PngSize(png);
                int side = Math.Max(w, h);
                if (!max.Success || side <= 0)
                    continue;
                int limit = int.Parse(max.Groups[1].Value);
                if (limit < side)
                    result[collection + "/" + Path.GetFileNameWithoutExtension(png)] = limit / (double)side;
            }
        }
        return result;
    }

    static (int w, int h) PngSize(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var header = new byte[24];
            if (f.Read(header, 0, 24) < 24 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G')
                return (0, 0);
            int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return (w, h);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }
}

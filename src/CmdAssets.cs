using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Material;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Writers.UEFormat.Enums;
using Newtonsoft.Json.Linq;

namespace Uex;

static partial class Cmds
{
    static bool IsPackage(string k) => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    // ---- info / ls / search / types / props / raw ------------------------------------------------

    public static object Info(Ctx c, CliArgs a)
    {
        var s = c.Session; var p = s.Provider;
        return new
        {
            id = s.GameId,
            name = s.Profile.Name,
            version = Versions.Name(s.Game),
            paks = s.Profile.Paks,
            projectName = p.ProjectName,
            files = p.Files.Count,
            packages = p.Files.Keys.Count(IsPackage),
            archives = new { mounted = p.MountedVfs.Count, locked = s.Locked.Count },
            requiredKeyGuids = s.Locked.Select(v => v.EncryptionKeyGuid.ToString()).Distinct(),
            keyResults = s.KeyResults,
            mappings = s.MappingsPath,
            natives = s.NativeState,
            topFolders = p.Files.Keys.Select(k => k.Split('/')[0]).GroupBy(x => x, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(20).Select(g => new { folder = g.Key, files = g.Count() }),
        };
    }

    public static object Ls(Ctx c, CliArgs a)
    {
        var p = c.Session.Provider;
        var prefix = (a.Pos.Count > 0 ? a.Pos[0] : "").Replace('\\', '/').Trim('/');
        var pre = prefix.Length == 0 ? "" : prefix + "/";
        bool all = a.Flag("all");
        var dirs = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var files = new List<(string name, long size)>();
        foreach (var kv in p.Files)
        {
            var path = kv.Key;
            if (!path.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[pre.Length..];
            int slash = rest.IndexOf('/');
            if (slash >= 0) { var d = rest[..slash]; dirs[d] = dirs.GetValueOrDefault(d) + 1; }
            else if (all || IsPackage(rest)) files.Add((rest, kv.Value.Size));
        }
        if (dirs.Count == 0 && files.Count == 0 && prefix.Length > 0) throw c.Session.NotFound(prefix);
        var entries = dirs.Select(d => (object)new { type = "dir", name = d.Key, files = d.Value })
            .Concat(files.OrderBy(f => f.name, StringComparer.OrdinalIgnoreCase).Select(f => (object)new { type = "file", name = f.name, bytes = f.size })).ToList();
        int off = a.Int("offset", 0), lim = a.Int("limit", 500);
        var page = entries.Skip(off).Take(lim).ToList();
        return new { path = prefix, dirs = dirs.Count, files = files.Count, total = entries.Count, offset = off, limit = lim, truncated = off + page.Count < entries.Count, entries = page };
    }

    public static object Search(Ctx c, CliArgs a)
    {
        var p = c.Session.Provider;
        var pattern = a.Need(0, "regex");
        Regex rx;
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
        catch (ArgumentException e) { throw new UexError("bad_argument", "invalid regex: " + e.Message); }
        var ext = a.Opt("ext")?.TrimStart('.');
        bool all = a.Flag("all");
        var hits = p.Files.Keys.Where(k => ext != null ? k.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase) : all || IsPackage(k))
                    .Where(k => rx.IsMatch(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        int off = a.Int("offset", 0), lim = a.Int("limit", 200);
        var page = hits.Skip(off).Take(lim).ToList();
        return new { pattern, ext, total = hits.Count, offset = off, limit = lim, truncated = off + page.Count < hits.Count, items = page };
    }

    public static object Types(Ctx c, CliArgs a)
    {
        var pkg = c.Session.LoadPackage(a.Need(0, "package"), out var norm);
        var exports = pkg.GetExports().Select((e, i) => new { index = i, @class = e.ExportType, name = e.Name, outer = e.Outer?.Name }).ToList();
        return new { package = norm, count = exports.Count, exports };
    }

    public static object Props(Ctx c, CliArgs a)
    {
        var pkg = c.Session.LoadPackage(a.Need(0, "package"), out var norm);
        var exports = pkg.GetExports().ToList();
        var en = a.Opt("export"); var cl = a.Opt("class");
        if (en != null) exports = exports.Where(e => e.Name.Equals(en, StringComparison.OrdinalIgnoreCase)).ToList();
        if (cl != null) exports = exports.Where(e => e.ExportType.Equals(cl, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exports.Count == 0 && (en != null || cl != null))
            throw new UexError("not_found", $"no export matching --export/--class in {norm}", "run `uex types <package>`");
        var json = Output.UeJson(exports, a.Opt("out") != null);
        var outFile = a.Opt("out");
        if (outFile != null)
        {
            outFile = Path.GetFullPath(Paths.Expand(outFile));
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            File.WriteAllText(outFile, json);
            return new { package = norm, exports = exports.Count, file = outFile, bytes = new FileInfo(outFile).Length };
        }
        int max = a.Int("max-bytes", 200_000);
        if (json.Length > max)
            throw new UexError("output_too_large", $"properties are {json.Length} chars (limit {max})", "write them to a file with --out <file.json> (then grep it), or narrow with --export <name> / --class <class>",
                               new { chars = json.Length, exports = exports.Select(e => new { @class = e.ExportType, name = e.Name }).Take(50) });
        return new { package = norm, exports = JToken.Parse(json) };
    }

    public static object Raw(Ctx c, CliArgs a)
    {
        var p = c.Session.Provider;
        if (a.Pos.Count == 0) throw new UexError("usage", "missing argument <path>");
        var dir = Path.GetFullPath(Paths.Expand(a.Opt("out") ?? "uex_export"));
        bool tree = a.Flag("keep-tree");
        var items = new List<object>(); int bad = 0;
        foreach (var arg in a.Pos)
        {
            var key = arg.Replace('\\', '/').TrimStart('/');
            if (!p.TryGetGameFile(key, out var gf) && a.Pos.Count == 1) throw c.Session.NotFound(arg);
            if (gf == null) { bad++; var nf = c.Session.NotFound(arg); items.Add(new { path = arg, ok = false, error = nf.Message, details = nf.Details }); continue; }
            try
            {
                var dst = tree ? Path.Combine(dir, gf.Path.Replace('/', Path.DirectorySeparatorChar)) : Path.Combine(dir, gf.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                var bytes = gf.Read();
                File.WriteAllBytes(dst, bytes);
                items.Add(new { path = gf.Path, ok = true, file = dst, bytes = bytes.Length });
            }
            catch (Exception e) { bad++; items.Add(new { path = gf.Path, ok = false, error = Output.Unwrap(e) }); }
        }
        if (bad > 0 && bad == a.Pos.Count && a.Pos.Count == 1) throw new UexError("not_found", ((dynamic)items[0]).error, null, items[0]);
        c.Partial = bad > 0;
        return new { outDir = dir, ok = items.Count - bad, failed = bad, items };
    }

    // ---- export / map ----------------------------------------------------------------------------

    static ExportOptions Options(ExportConfig cfg, CliArgs a, EMeshFormat? forceMesh = null)
    {
        var mesh = forceMesh ?? ParseEnum<EMeshFormat>(a.Opt("mesh-format") ?? cfg.MeshFormat, "mesh-format");
        var tex = ParseEnum<ETextureFormat>(a.Opt("texture-format") ?? cfg.TextureFormat, "texture-format");
        var depth = ParseEnum<EMaterialDepth>(a.Opt("material-depth") ?? cfg.MaterialDepth, "material-depth");
        var quality = ParseEnum<EMeshQuality>(a.Opt("mesh-quality") ?? cfg.MeshQuality, "mesh-quality");
        return new ExportOptions(
            meshFormat: mesh, naniteMeshFormat: ENaniteMeshFormat.NoNanite, meshQuality: quality,
            texturePlatform: ETexturePlatform.DesktopMobile, textureFormat: tex, textureQuality: 100,
            exportHdrTexturesAsHdr: false, exportAllTextureMips: false, materialDepth: depth,
            exportMaterials: cfg.ExportMaterials && !a.Flag("no-materials"), exportMorphTargets: false, socketFormat: ESocketFormat.None,
            compressionFormat: EFileCompressionFormat.None, pngCompressionLevel: 6, tgaRleCompression: false,
            includeDisabledMeshSections: false);
    }

    static readonly string[] SessionTypes = { "StaticMesh", "SkeletalMesh", "Texture2D", "TextureCube", "VirtualTexture2D", "AnimSequence", "MaterialInstanceConstant", "Material" };

    public static object Export(Ctx c, CliArgs a)
    {
        var paths = new List<string>(a.Pos);
        if (a.Opt("list") is { } list)
        {
            var lines = list == "-" ? ReadAllStdin() : File.Exists(Paths.Expand(list)) ? File.ReadAllLines(Paths.Expand(list)).ToList()
                      : throw new UexError("bad_argument", $"--list file not found: {list}");
            paths.AddRange(lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")));
        }
        if (paths.Count == 0) throw new UexError("usage", "nothing to export", "give package paths and/or --list <file|->");

        var s = c.Session;
        var outDir = Path.GetFullPath(Paths.Expand(a.Opt("out") ?? "uex_export"));
        var opts = Options(s.Profile.Export ?? new ExportConfig(), a);
        var items = new List<object>(); int bad = 0;
        foreach (var path in paths)
        {
            string norm = Session.Norm(path);
            try
            {
                var pkg = s.LoadPackage(path, out norm);
                var exports = pkg.GetExports().ToList();
                var main = exports.FirstOrDefault(e => SessionTypes.Contains(e.ExportType));
                if (main != null)
                {
                    if (main.ExportType == "AnimSequence" && s.NativeState.TryGetValue("acl", out var acl) && !acl)
                        c.Warnings.Add("ACL native missing: AnimSequence export will likely fail (`uex setup natives`)");
                    var es = new ExportSession(null);
                    es.Add(main);
                    Directory.CreateDirectory(outDir);
                    var res = es.RunAsync(outDir, opts, null, CancellationToken.None).GetAwaiter().GetResult();
                    var files = res.Where(r => r.Success).SelectMany(r => r.DiskFilePaths ?? Array.Empty<string>()).Distinct().ToList();
                    var errs = res.Where(r => !r.Success).Select(r => $"{r.ObjectPath}: {(r.Error == null ? "failed" : Output.Unwrap(r.Error))}").ToList();
                    bool ok = errs.Count == 0;
                    if (!ok) bad++;
                    items.Add(new { path = norm, ok, @class = main.ExportType, files, errors = errs.Count > 0 ? errs : null });
                    continue;
                }
                var file = Path.Combine(outDir, norm.TrimStart('/').Replace('/', Path.DirectorySeparatorChar) + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, Output.UeJson(exports));
                items.Add(new { path = norm, ok = true, @class = exports.FirstOrDefault()?.ExportType, files = new[] { file }, asJson = true });
            }
            catch (UexError e) { bad++; items.Add(new { path = norm, ok = false, error = e.Message, code = e.Code, details = e.Details }); }
            catch (Exception e) { bad++; items.Add(new { path = norm, ok = false, error = Output.Unwrap(e), code = "export_failed" }); }
        }
        if (bad == paths.Count && paths.Count == 1 && items[0] is { } only && only.GetType().GetProperty("code")?.GetValue(only) is string code)
            throw new UexError(code, (string)only.GetType().GetProperty("error")!.GetValue(only), null, only.GetType().GetProperty("details")?.GetValue(only));
        c.Partial = bad > 0;
        return new { outDir, ok = items.Count - bad, failed = bad, items };
    }

    public static object Map(Ctx c, CliArgs a)
    {
        var s = c.Session;
        var pkg = s.LoadPackage(a.Need(0, "package"), out var norm);
        var exports = pkg.GetExports().ToList();
        var fmt = (a.Opt("format") ?? "usd").ToLowerInvariant() switch
        {
            "actorx" => EMeshFormat.ActorX, "gltf" or "gltf2" => EMeshFormat.Gltf2, "ueformat" => EMeshFormat.UEFormat, "usd" => EMeshFormat.USD,
            var f => throw new UexError("bad_argument", $"--format: unknown '{f}'", "usd | actorx | gltf | ueformat"),
        };
        int seed = a.Int("seed", 0);
        var outDir = Path.GetFullPath(Paths.Expand(a.Opt("out") ?? "uex_export"));
        CUE4Parse_Conversion.Exporters.ExporterBase exp = null; string used = null;
        foreach (var t in new[] { typeof(CUE4Parse_Conversion.Exporters.Custom.DeadByDaylightMapExporter), typeof(CUE4Parse_Conversion.Exporters.WorldExporter) })
        {
            var ctor = t.GetConstructors()[0]; var pt = ctor.GetParameters()[0].ParameterType;
            var target = exports.FirstOrDefault(e => pt.IsInstanceOfType(e));
            if (target == null) continue;
            var args = ctor.GetParameters().Length == 2 ? new object[] { target, seed } : new object[] { target };
            exp = (CUE4Parse_Conversion.Exporters.ExporterBase)ctor.Invoke(args);
            used = $"{t.Name} on {target.ExportType} '{target.Name}'";
            break;
        }
        if (exp == null) throw new UexError("bad_argument", $"{norm} holds no World or PaperTileMap", "use `uex export` for single assets", new { classes = exports.Select(e => e.ExportType).Distinct() });
        var es = new ExportSession(null);
        es.Add(exp);
        Directory.CreateDirectory(outDir);
        var res = es.RunAsync(outDir, Options(s.Profile.Export ?? new ExportConfig(), a, fmt), null, CancellationToken.None).GetAwaiter().GetResult();
        var failed = res.Where(r => !r.Success).Select(r => $"{r.ObjectPath}: {(r.Error == null ? "failed" : Output.Unwrap(r.Error))}").ToList();
        c.Partial = failed.Count > 0;
        return new { package = norm, exporter = used, outDir, files = res.Sum(r => r.DiskFilePaths?.Count ?? 0), failedCount = failed.Count, failed = failed.Take(50) };
    }

    // ---- batch -----------------------------------------------------------------------------------

    public static object Batch(Ctx c, CliArgs a)
    {
        var src = a.Need(0, "file|-");
        var lines = src == "-" ? ReadAllStdin() : File.Exists(Paths.Expand(src)) ? File.ReadAllLines(Paths.Expand(src)).ToList()
                  : throw new UexError("bad_argument", $"batch file not found: {src}");
        bool stop = a.Flag("stop-on-error");
        _ = c.Session;                                  // mount once, up front (errors abort the batch)
        var mountWarnings = c.Warnings.ToList();
        c.InBatch = true;
        int n = 0, ok = 0, failed = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            n++;
            var tokens = CliArgs.Tokenize(line);
            if (tokens.Count > 0 && tokens[0].Equals("uex", StringComparison.OrdinalIgnoreCase)) tokens.RemoveAt(0);
            int code;
            if (tokens.Count > 0 && tokens[0].Equals("batch", StringComparison.OrdinalIgnoreCase))
            {
                Output.Write(Output.Fail("batch", c.GameId, new UexError("usage", "nested batch is not allowed"), null), true); code = 2;
            }
            else
            {
                CliArgs la;
                try { la = CliArgs.Parse(tokens); }
                catch (UexError e) { Output.Write(Output.Fail(null, c.GameId, e, null), true); failed++; if (stop) break; continue; }
                if (la.Opt("game") is { } g && !g.Equals(c.GameId, StringComparison.OrdinalIgnoreCase))
                { Output.Write(Output.Fail(null, c.GameId, new UexError("usage", $"batch is mounted on '{c.GameId}', line asks for '{g}'"), null), true); failed++; if (stop) break; continue; }
                code = Program.Run(c, la);
            }
            if (code == 0) ok++; else failed++;
            if (code != 0 && stop) break;
        }
        c.InBatch = false;
        c.Warnings = mountWarnings;
        c.Partial = failed > 0;
        return new { summary = true, lines = n, ok, failed };
    }

    static List<string> ReadAllStdin()
    {
        var l = new List<string>(); string s;
        while ((s = Console.In.ReadLine()) != null) l.Add(s);
        return l;
    }
}

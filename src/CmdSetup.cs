using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;

namespace Uex;

static partial class Cmds
{
    // ---- doctor / versions -----------------------------------------------------------------------

    public static object Doctor(Ctx c, CliArgs a) => new
    {
        uex = Program.Version,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
        dotnet = Environment.Version.ToString(),
        configPath = c.ConfigPath,
        configExists = File.Exists(c.ConfigPath),
        nativesDir = c.NativesDir,
        natives = Natives.Status(c.NativesDir),
        defaultGame = c.Config.DefaultGame,
        games = c.Config.Games.Select(g => new { id = g.Key, name = g.Value.Name, version = g.Value.Version, paksExists = Directory.Exists(Paths.Expand(g.Value.Paks ?? "")) }),
        nextSteps = c.Config.Games.Count == 0
            ? new[] { "uex setup natives --from <folder with oodle/zlib/CUE4Parse-Natives>  (or --download)", "uex setup detect <game install folder>" }
            : new[] { "uex info -g <id>" },
    };

    public static object ListVersions(Ctx c, CliArgs a)
    {
        var f = a.Opt("filter");
        var rx = f == null ? null : new Regex(f, RegexOptions.IgnoreCase);
        var items = Versions.All.Where(n => rx == null || rx.IsMatch(n)).ToList();
        return new { count = items.Count, items };
    }

    // ---- setup detect ----------------------------------------------------------------------------

    sealed record ArchDir(string Path, int Pak, int Utoc, int Ucas, int Sig, long Bytes);

    public static object Detect(Ctx c, CliArgs a)
    {
        var root = Path.GetFullPath(Paths.Expand(a.Need(0, "game folder")));
        if (File.Exists(root)) root = Path.GetDirectoryName(root)!;
        if (!Directory.Exists(root)) throw new UexError("paks_not_found", $"folder not found: {root}", "pass the game's install folder (Steam: steamapps/common/<Game>)");
        int depth = a.Int("depth", 6);

        // 1. every folder holding archives
        var dirs = new List<ArchDir>();
        int visited = 0;
        Walk(root, depth, d =>
        {
            if (++visited > 50000) return false;
            var files = SafeFiles(d);
            int pak = files.Count(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase));
            int utoc = files.Count(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
            if (pak + utoc > 0)
            {
                long bytes = files.Where(f => Regex.IsMatch(f, @"\.(pak|utoc|ucas)$", RegexOptions.IgnoreCase)).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
                dirs.Add(new ArchDir(d, pak, utoc, files.Count(f => f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase)), files.Count(f => f.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)), bytes));
            }
            return true;
        });
        if (dirs.Count == 0)
            throw new UexError("paks_not_found", $"no .pak/.utoc under {root} (depth {depth})", "pass the install folder itself, or raise --depth", new { visitedFolders = visited });

        // 2. primary = biggest <X>/Content/Paks, else biggest folder
        var primary = dirs.Where(d => Path.GetFileName(d.Path).Equals("Paks", StringComparison.OrdinalIgnoreCase)
                                      && Path.GetFileName(Path.GetDirectoryName(d.Path) ?? "").Equals("Content", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(d => d.Bytes).FirstOrDefault() ?? dirs.OrderByDescending(d => d.Bytes).First();
        var nested = dirs.Where(d => d != primary && IsUnder(d.Path, primary.Path)).ToList();
        var others = dirs.Where(d => d != primary && !IsUnder(d.Path, primary.Path)).ToList();

        var projectDir = Path.GetFileName(Path.GetDirectoryName(primary.Path) ?? "").Equals("Content", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.GetDirectoryName(primary.Path)) : Path.GetDirectoryName(primary.Path);
        var project = Path.GetFileName(projectDir);
        var gameRoot = IsUnder(projectDir, root) && !SamePath(projectDir, root) ? root : Path.GetDirectoryName(projectDir) ?? root;

        // 3. executables -> engine version string (++UE5+Release-5.3)
        var exes = FindExecutables(gameRoot, 5)
            .Where(f => !Regex.IsMatch(f.Name, @"^(CrashReportClient|EpicWebHelper|UnrealCEFSubProcess|UE4PrereqSetup|UEPrereqSetup|BootstrapPackagedGame|EasyAntiCheat|start_protected_game|.*Launcher.*)", RegexOptions.IgnoreCase))
            .OrderByDescending(f => projectDir != null && IsUnder(f.FullName, projectDir) ? 1 : 0).ThenByDescending(f => f.Length).Take(6).ToList();
        object engine = null; int maj = 0, min = 0;
        var exeInfo = new List<object>();
        foreach (var f in exes)
        {
            string found = null;
            if (engine == null)
            {
                var r = ScanEngine(f.FullName);
                if (r != null) { (maj, min, found) = r.Value; engine = new { raw = found, major = maj, minor = min, source = f.FullName }; }
            }
            exeInfo.Add(new { path = f.FullName, bytes = f.Length, engineString = found });
        }

        // 4. version candidates
        var hints = new List<string> { project, Path.GetFileName(gameRoot) }.Concat(exes.Select(e => Path.GetFileNameWithoutExtension(e.Name).Split('-')[0])).ToList();
        var specific = Versions.MatchGame(hints);
        var generic = engine != null ? Versions.Generic(maj, min) : null;
        var recommended = specific.FirstOrDefault() ?? generic;
        var id = Regex.Replace((project ?? "game").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (id.Length == 0) id = "game";

        var steps = new List<string>();
        var vArg = recommended ?? "<version: see `uex versions`>";
        var rec = nested.Count > 0 ? " --recursive" : "";
        steps.Add($"uex setup probe --paks \"{primary.Path}\" --version {vArg}{rec}");
        steps.Add("if the probe reports requiredKeys: get the AES key(s) from the user, add --aes <key> (main, guid 0) or --aes-key <guid>=<key>, probe again");
        steps.Add("if the probe verdict is needs_mappings: get a .usmap for this game version from the user, add --mappings <file>");
        steps.Add($"when the verdict is ok: same probe command + --save {id} --default");

        return new
        {
            root,
            gameRoot,
            project,
            suggestedId = id,
            paks = primary.Path,
            recursiveRecommended = nested.Count > 0,
            archiveDirs = dirs.Select(d => new { path = d.Path, pak = d.Pak, utoc = d.Utoc, ucas = d.Ucas, sig = d.Sig, mb = Math.Round(d.Bytes / 1048576.0, 1),
                                                 role = d == primary ? "primary" : nested.Contains(d) ? "nested (use --recursive)" : "other (maybe --extra-paks)" }),
            ioStore = primary.Utoc > 0,
            engine,
            executables = exeInfo,
            versionCandidates = new { gameSpecific = specific, generic },
            recommendedVersion = recommended,
            notes = new[]
            {
                engine == null ? "engine version string not found in executables: try GAME_UE5_LATEST / GAME_UE4_LATEST or ask the user" : null,
                specific.Count > 0 ? "a game-specific CUE4Parse version exists; prefer it over the generic one (custom engine forks)" : null,
                primary.Utoc > 0 && (engine == null || maj >= 5) ? "IoStore + UE5: a .usmap mappings file is usually required" : null,
            }.Where(x => x != null),
            nextSteps = steps,
        };
    }

    static bool IsUnder(string p, string parent) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, '/').StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || SamePath(p, parent);
    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    static List<string> SafeFiles(string d) { try { return Directory.EnumerateFiles(d).ToList(); } catch { return new List<string>(); } }

    static void Walk(string d, int depth, Func<string, bool> visit)
    {
        if (!visit(d) || depth <= 0) return;
        IEnumerable<string> subs;
        try { subs = Directory.EnumerateDirectories(d).ToList(); } catch { return; }
        foreach (var s in subs)
        {
            try { if (new DirectoryInfo(s).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
            Walk(s, depth - 1, visit);
        }
    }

    static List<FileInfo> FindExecutables(string root, int depth)
    {
        var r = new List<FileInfo>();
        Walk(root, depth, d =>
        {
            var name = Path.GetFileName(d);
            bool binDir = SamePath(d, root) || Path.GetFileName(Path.GetDirectoryName(d) ?? "").Equals("Binaries", StringComparison.OrdinalIgnoreCase);
            if (binDir)
                foreach (var f in SafeFiles(d))
                {
                    var fi = new FileInfo(f);
                    if (fi.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || (fi.Extension.Length == 0 && IsElf(f))) r.Add(fi);
                }
            return !name.Equals("Content", StringComparison.OrdinalIgnoreCase);   // never descend into Content (huge)
        });
        return r;
    }

    static bool IsElf(string f)
    {
        try { using var s = File.OpenRead(f); var b = new byte[4]; return s.Read(b, 0, 4) == 4 && b[0] == 0x7F && b[1] == 'E' && b[2] == 'L' && b[3] == 'F'; }
        catch { return false; }
    }

    static readonly Regex EngineAscii = new(@"\+\+UE([45])\+Release-(\d)\.(\d{1,2})", RegexOptions.Compiled);
    static readonly Regex EngineUtf16 = new(@"\+\0\+\0U\0E\0([45])\0\+\0R\0e\0l\0e\0a\0s\0e\0-\0(\d)\0\.\0(\d)\0(\d)?", RegexOptions.Compiled);

    // PE VS_VERSION_INFO string table (UTF-16): "FileVersion\0" / "ProductVersion\0", padding, then the value.
    // Unreal stamps the ENGINE version there by default (e.g. 5.5.4); some games overwrite it with their own.
    static readonly Regex PeVersion = new(@"(?:F\0i\0l\0e\0|P\0r\0o\0d\0u\0c\0t\0)V\0e\0r\0s\0i\0o\0n\0\0\0(?:\0\0)?((?:[0-9.]\0){3,24})\0\0", RegexOptions.Compiled);

    /// <summary>
    /// Stream the binary (latin1 = one char per byte). Best: the branch string "++UE5+Release-5.3" (ASCII or UTF-16).
    /// Fallback: a 4.x/5.x PE FileVersion/ProductVersion. Parsed by hand so it also works on Linux.
    /// </summary>
    static (int, int, string)? ScanEngine(string file)
    {
        (int, int, string)? fallback = null;
        try
        {
            using var s = File.OpenRead(file);
            var buf = new byte[8 << 20]; int overlap = 256, carry = 0;
            while (true)
            {
                int n = s.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                int len = carry + n;
                var text = Encoding.Latin1.GetString(buf, 0, len);
                var m = EngineAscii.Match(text);
                if (m.Success) return (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), m.Value);
                m = EngineUtf16.Match(text);
                if (m.Success)
                {
                    var minor = m.Groups[3].Value + (m.Groups[4].Success ? m.Groups[4].Value : "");
                    return (int.Parse(m.Groups[2].Value), int.Parse(minor), m.Value.Replace("\0", ""));
                }
                if (fallback == null)
                    foreach (Match pm in PeVersion.Matches(text))
                    {
                        var v = pm.Groups[1].Value.Replace("\0", "");
                        var vm = Regex.Match(v, @"^([45])\.(\d{1,2})(?:\.\d+)*$");
                        if (vm.Success) { fallback = (int.Parse(vm.Groups[1].Value), int.Parse(vm.Groups[2].Value), $"PE version {v} (engine version by default, unless the game overrides it)"); break; }
                    }
                carry = Math.Min(overlap, len);
                Buffer.BlockCopy(buf, len - carry, buf, 0, carry);
            }
        }
        catch { }
        return fallback;
    }

    // ---- setup probe ------------------------------------------------------------------------------

    public static object Probe(Ctx c, CliArgs a)
    {
        // start from a saved profile (if --game) then apply inline overrides; never touches the config unless --save
        GameProfile prof; string baseId = null;
        if (a.Opt("game") != null || (!a.Has("paks") && c.Config.Games.Count > 0))
        {
            var (pid, saved0) = ConfigStore.Pick(c.Config, a.Opt("game"));
            baseId = pid; prof = Clone(saved0);
        }
        else prof = new GameProfile();
        ApplyProfileOptions(prof, a, c.Warnings, creating: baseId == null);
        if (string.IsNullOrEmpty(prof.Paks)) throw new UexError("usage", "probe needs --paks <dir> (or --game <id>)");
        if (string.IsNullOrEmpty(prof.Version)) throw new UexError("usage", "probe needs --version <v>", "`uex setup detect <game folder>` recommends one");

        using var s = Session.Open(baseId ?? "(probe)", prof, c.NativesDir, c.Warnings);
        var p = s.Provider;

        var locked = s.Locked;
        var required = locked.GroupBy(v => v.EncryptionKeyGuid.ToString())
            .Select(g => new { guid = g.Key, isMainKey = Regex.IsMatch(g.Key, "^0+$"), archives = g.Count(), sample = g.Take(3).Select(v => v.Name) }).ToList();

        // sample load: N packages spread over the sorted list
        int n = a.Int("sample", 12);
        var pkgs = p.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        var picks = pkgs.Count == 0 ? new List<string>() : Enumerable.Range(0, Math.Min(n, pkgs.Count)).Select(i => pkgs[(int)((long)i * pkgs.Count / Math.Min(n, pkgs.Count))]).Distinct().ToList();
        int ok = 0; var errors = new List<object>();
        foreach (var k in picks)
        {
            try { var pk = p.LoadPackage(k); var ex = pk.GetExports().ToList(); _ = Output.UeJson(ex, false); ok++; }
            catch (Exception e) { errors.Add(new { path = k, error = Output.Unwrap(e) }); }
        }
        int failed = picks.Count - ok;
        string allErr = string.Join(" ", errors.Select(e => e.ToString()));

        var hints = new List<string>();
        string verdict;
        if (pkgs.Count == 0 && locked.Count > 0) { verdict = "needs_aes"; hints.Add("every archive is locked: provide the AES key(s) for requiredKeys[].guid (--aes for the all-zero guid, --aes-key <guid>=<key> for others)"); }
        else if (pkgs.Count == 0) { verdict = "no_packages"; hints.Add("nothing mounted: wrong folder, or --recursive needed, or archives need natives (oodle)"); }
        else if (picks.Count > 0 && failed * 2 > picks.Count)
        {
            verdict = s.MappingsPath == null ? "needs_mappings" : "load_errors";
            hints.Add(s.MappingsPath == null ? "most sample packages fail and no .usmap is set: add --mappings <file.usmap> (dumped from the game, matching its current version)"
                                             : "most sample packages fail: try another --version (game-specific one first, see `uex versions --filter <game>`) or a newer .usmap");
            if (Regex.IsMatch(allErr, "oodle", RegexOptions.IgnoreCase)) hints.Add("errors mention Oodle: run `uex setup natives`");
        }
        else if (locked.Count > 0) { verdict = "partial_aes"; hints.Add($"{locked.Count} archive(s) still locked (often DLC/optional chunks); add their keys if you need their content"); }
        else if (failed > 0) { verdict = "ok_with_errors"; hints.Add("a few sample packages fail; usually fine (exotic classes). Re-check with the assets you actually need"); }
        else verdict = "ok";

        object saved = null;
        var saveId = a.Opt("save");
        if (saveId != null)
        {
            ConfigStore.ValidateId(saveId);
            if (c.Config.Games.ContainsKey(saveId) && !a.Flag("force") && !string.Equals(saveId, baseId, StringComparison.OrdinalIgnoreCase))
                throw new UexError("game_exists", $"profile '{saveId}' exists", "add --force to overwrite");
            c.Config.Games[saveId] = prof;
            if (a.Flag("default") || c.Config.DefaultGame == null) c.Config.DefaultGame = saveId;
            c.SaveConfig();
            saved = new { id = saveId, configPath = c.ConfigPath, isDefault = c.Config.DefaultGame == saveId };
        }

        return new
        {
            verdict,
            hints,
            profile = Describe(prof),
            version = Versions.Name(s.Game),
            archives = new { mounted = p.MountedVfs.Count, locked = locked.Count },
            requiredKeys = required,
            keyResults = s.KeyResults,
            files = p.Files.Count,
            packages = pkgs.Count,
            mappingsUsed = s.MappingsPath,
            natives = s.NativeState,
            sample = new { tried = picks.Count, ok, failed, errors = errors.Take(6) },
            saved,
            nextSteps = saved != null ? new[] { $"uex info -g {saveId}" } : verdict.StartsWith("ok") ? new[] { "rerun the same probe with --save <id> [--default]" } : Array.Empty<string>(),
        };
    }

    static GameProfile Clone(GameProfile p) =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<GameProfile>(Newtonsoft.Json.JsonConvert.SerializeObject(p));

    // ---- setup natives ----------------------------------------------------------------------------

    public static object SetupNatives(Ctx c, CliArgs a)
    {
        var dir = c.NativesDir;
        var copied = new List<object>();
        var froms = a.Multi("from").Select(f => Path.GetFullPath(Paths.Expand(f))).ToList();
        foreach (var from in froms)
        {
            if (!Directory.Exists(from)) { c.Warnings.Add($"--from folder not found: {from}"); continue; }
            foreach (var lib in Natives.Libs)
            {
                if (Natives.Find(lib, dir) != null && !a.Flag("force")) continue;
                var names = Natives.NamesForOs(lib).Distinct().ToList();
                string hit = null;
                Walk(from, 3, d =>
                {
                    if (hit != null) return false;
                    foreach (var nme in names) { var p = Path.Combine(d, nme); if (File.Exists(p)) { hit = p; return false; } }
                    return true;
                });
                if (hit == null) continue;
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, Path.GetFileName(hit));
                File.Copy(hit, dst, true);
                copied.Add(new { id = lib.Id, from = hit, to = dst });
            }
        }

        var downloaded = new List<object>();
        if (a.Flag("download"))
        {
            Directory.CreateDirectory(dir);
            if (Natives.Find(Natives.Libs[0], dir) == null)
            {
                var dst = Path.Combine(dir, OperatingSystem.IsWindows() ? OodleHelper.OODLE_NAME_CURRENT : OodleHelper.OODLE_NAME_LINUX);
                using var http = new HttpClient();
                bool okDl = false; string err = null;
                try { okDl = OodleHelper.DownloadOodleDllFromOodleUEAsync(http, dst, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception e) { err = Output.Unwrap(e); }
                downloaded.Add(new { id = "oodle", ok = okDl && File.Exists(dst), path = dst, error = err });
            }
            if (Natives.Find(Natives.Libs[1], dir) == null)
            {
                var linux = OperatingSystem.IsLinux();
                var dst = Path.Combine(dir, linux ? ZlibHelper.DLL_NAME_LINUX : ZlibHelper.DLL_NAME);
                bool okDl = false; string err = null;
                try { okDl = ZlibHelper.DownloadDllAsync(dst, linux ? ZlibHelper.DOWNLOAD_URL_LINUX : ZlibHelper.DOWNLOAD_URL).GetAwaiter().GetResult(); }
                catch (Exception e) { err = Output.Unwrap(e); }
                downloaded.Add(new { id = "zlib", ok = okDl && File.Exists(dst), path = dst, error = err });
            }
            c.Warnings.Add("--download only covers oodle and zlib-ng; ACL (animations) and Detex must come from --from");
        }

        var status = Natives.Status(dir);
        return new
        {
            nativesDir = dir,
            copied,
            downloaded,
            natives = status,
            hints = new[]
            {
                "FModel (Windows) keeps oodle/zlib/Detex in <FModel Output>/.data and extracts CUE4Parse-Natives.dll (ACL) to %TEMP%/.net/FModel/<hash>/",
                OperatingSystem.IsWindows() ? null : "Linux: libCUE4Parse-Natives.so (ACL, animations) is not distributed; build it from the CUE4Parse repo (CUE4Parse-Natives, CMake) or skip animations",
            }.Where(h => h != null),
        };
    }
}

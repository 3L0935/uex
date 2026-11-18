using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Material;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

namespace Uex;

/// <summary>A mounted game. One per process (shared by every line of `uex batch`).</summary>
sealed class Session : IDisposable
{
    public string GameId;
    public GameProfile Profile;
    public EGame Game;
    public DefaultFileProvider Provider;
    public string MappingsPath;
    public Dictionary<string, bool> NativeState;
    public List<object> KeyResults = new();
    public int BadKeys;

    /// <summary>Archives still locked by a missing key. global.utoc (IoStore global container) is loaded separately and never counts.</summary>
    public List<CUE4Parse.UE4.VirtualFileSystem.IAesVfsReader> Locked => Provider.UnloadedVfs.Where(v => !v.Name.Equals("global.utoc", StringComparison.OrdinalIgnoreCase)).ToList();

    public void Dispose() => Provider?.Dispose();

    public static Session Open(string gameId, GameProfile prof, string nativesDir, List<string> warnings)
    {
        var s = new Session { GameId = gameId, Profile = prof };
        s.Game = Versions.Parse(prof.Version);
        var paks = Paths.Expand(prof.Paks);
        if (string.IsNullOrEmpty(paks) || !Directory.Exists(paks))
            throw new UexError("paks_not_found", $"paks folder not found: {paks}", "fix it with `uex games set <id> --paks <folder>`; `uex setup detect <game folder>` finds it");

        s.NativeState = Natives.Init(nativesDir, warnings);
        if (!s.NativeState["oodle"]) warnings.Add("oodle native missing: Oodle-compressed archives will fail to read (`uex setup natives`)");

        var platform = ETexturePlatform.DesktopMobile;
        if (!string.IsNullOrEmpty(prof.Platform) && !Enum.TryParse(prof.Platform, true, out platform))
            throw new UexError("bad_config", $"unknown texture platform '{prof.Platform}'", "valid: " + string.Join(", ", Enum.GetNames<ETexturePlatform>()));

        var extra = (prof.ExtraPaks ?? new()).Select(Paths.Expand).Where(Directory.Exists).Select(d => new DirectoryInfo(d)).ToArray();
        var opt = prof.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            s.Provider = new DefaultFileProvider(new DirectoryInfo(paks), extra, opt, new VersionContainer(s.Game, platform), StringComparer.OrdinalIgnoreCase);

            s.MappingsPath = ResolveMappings(prof.Mappings, warnings);
            if (s.MappingsPath != null) s.Provider.MappingsContainer = new FileUsmapTypeMappingsProvider(s.MappingsPath);

            s.Provider.Initialize();
            SubmitKeys(s, prof.Aes, warnings);
            s.Provider.PostMount();
        }
        catch (UexError) { throw; }
        catch (Exception e) { throw new UexError("mount_failed", "mount failed: " + Output.Unwrap(e), "check --version and the paks folder (`uex setup probe`)"); }

        var unloaded = s.Locked.Count;
        if (unloaded > 0)
            warnings.Add($"{unloaded} archive(s) still locked (missing AES keys) - their files are invisible. `uex setup probe` lists the key GUIDs");
        if (s.MappingsPath == null && (s.Game >= EGame.GAME_UE5_0 || s.Provider.MountedVfs.Any(v => v.GetType().Name.Contains("IoStore"))))
            warnings.Add("no .usmap mappings configured: unversioned UE5/IoStore packages may fail to parse (`uex games set <id> --mappings <file.usmap>`)");
        return s;
    }

    static string ResolveMappings(string m, List<string> warnings)
    {
        if (string.IsNullOrEmpty(m)) return null;
        m = Paths.Expand(m);
        if (File.Exists(m)) return m;
        if (Directory.Exists(m))
        {
            var f = new DirectoryInfo(m).GetFiles("*.usmap").OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
            if (f != null) return f.FullName;
            warnings.Add($"no .usmap in mappings folder {m}");
            return null;
        }
        warnings.Add($"mappings not found: {m}");
        return null;
    }

    static void SubmitKeys(Session s, AesConfig aes, List<string> warnings)
    {
        if (aes == null) return;
        void Submit(string guidText, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return;
            FGuid guid;
            try { guid = string.IsNullOrEmpty(guidText) ? new FGuid() : new FGuid(Keys.NormGuid(guidText)); }
            catch (Exception) { warnings.Add($"ignored AES key: bad GUID '{guidText}'"); return; }
            var aesKey = new FAesKey(Keys.NormKey(key));
            // SubmitKey alone can "mount" with a wrong key (garbage reads later look like a mappings problem):
            // test it on the locked archives of that GUID first.
            var targets = s.Provider.UnloadedVfs.Where(v => v.EncryptionKeyGuid == guid && v.IsEncrypted).ToList();
            int tested = 0, valid = 0;
            foreach (var v in targets.Take(8))
            {
                try { tested++; if (v.TestAesKey(aesKey)) valid++; }
                catch { }
            }
            if (tested > 0 && valid == 0)
            {
                s.KeyResults.Add(new { guid = guid.ToString(), valid = false, archivesMounted = 0, tested });
                warnings.Add($"AES key for guid {guid} is WRONG: it fails the decryption test on {tested} archive(s); not submitted");
                s.BadKeys++;
                return;
            }
            int n;
            try { n = s.Provider.SubmitKey(guid, aesKey); }
            catch (Exception e) { warnings.Add($"AES key for guid {guid} rejected: {Output.Unwrap(e)}"); return; }
            s.KeyResults.Add(new { guid = guid.ToString(), valid = tested > 0 ? true : (bool?)null, archivesMounted = n });
        }
        Submit(null, aes.Main);
        foreach (var kv in aes.Dynamic ?? new()) Submit(kv.Key, kv.Value);
    }

    // ---- paths ---------------------------------------------------------------------------------

    /// <summary>Accepts mount paths (Game/Content/X.uasset), /Game/X, /Game/X.X, ObjectPath "/Game/X.X:Sub", Class'/Game/X.X', "...X.0".</summary>
    public static string Norm(string path)
    {
        path = path.Trim().Trim('"').Replace('\\', '/');
        // Git Bash / MSYS rewrites "/Game/X" into "C:/Program Files/Git/Game/X" before we see it: undo that
        var msys = Regex.Match(path, @"^[A-Za-z]:/(?:[^/]+/)*?Git/(?:usr/)?(?<rest>[A-Za-z0-9_]+/.+)$", RegexOptions.IgnoreCase);
        if (msys.Success && !File.Exists(path)) path = "/" + msys.Groups["rest"].Value;
        path = Regex.Replace(path, @"^[A-Za-z0-9_]+'(.*)'$", "$1");
        int colon = path.IndexOf(':'); if (colon > 0 && !Regex.IsMatch(path, "^[A-Za-z]:/")) path = path[..colon];
        path = Regex.Replace(path, @"\.\d+$", "");
        var dot = path.LastIndexOf('.'); var slash = path.LastIndexOf('/');
        if (dot > slash && !Regex.IsMatch(path, @"\.(uasset|umap|uexp|ubulk|uptnl)$", RegexOptions.IgnoreCase)) path = path[..dot];
        return path;
    }

    public IPackage LoadPackage(string path, out string norm)
    {
        norm = Norm(path);
        if (!Provider.TryGetGameFile(norm, out _) && !Provider.TryGetGameFile(norm + ".uasset", out _) && !Provider.TryGetGameFile(norm + ".umap", out _))
            throw NotFound(path);
        try { return Provider.LoadPackage(norm); }
        catch (Exception e) { throw new UexError("load_failed", $"cannot load {norm}: {Output.Unwrap(e)}", MappingsPath == null ? "maybe missing .usmap mappings or wrong --version" : "maybe wrong --version"); }
    }

    public UexError NotFound(string path)
    {
        var leaf = Path.GetFileNameWithoutExtension(Norm(path).TrimEnd('/'));
        var near = string.IsNullOrEmpty(leaf) ? new List<string>() :
            Provider.Files.Keys.Where(k => Path.GetFileNameWithoutExtension(k).Equals(leaf, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        if (near.Count == 0 && leaf?.Length >= 4)
            near = Provider.Files.Keys.Where(k => k.Contains(leaf, StringComparison.OrdinalIgnoreCase) && (k.EndsWith(".uasset") || k.EndsWith(".umap"))).Take(5).ToList();
        return new UexError("not_found", $"no such file in the mounted archives: {Norm(path)}", near.Count > 0 ? "did you mean one of details.didYouMean?" : "use `uex search <regex>`", near.Count > 0 ? new { didYouMean = near } : null);
    }
}

static class Keys
{
    public static string NormKey(string k)
    {
        k = (k ?? "").Trim();
        if (k.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) k = k[2..];
        if (!Regex.IsMatch(k, "^[0-9A-Fa-f]{64}$"))
            throw new UexError("bad_key", "AES key must be 64 hex characters (optionally prefixed by 0x)");
        return "0x" + k.ToUpperInvariant();
    }

    public static string NormGuid(string g)
    {
        g = (g ?? "").Trim().Replace("-", "").Replace("{", "").Replace("}", "");
        if (!Regex.IsMatch(g, "^[0-9A-Fa-f]{32}$"))
            throw new UexError("bad_key", $"key GUID must be 32 hex characters, got '{g}'", "copy it from `uex setup probe` (requiredKeys[].guid)");
        return g.ToUpperInvariant();
    }

    /// <summary>Never print keys: only whether they are set.</summary>
    public static object Describe(AesConfig a) => new
    {
        main = !string.IsNullOrEmpty(a?.Main),
        dynamic = a?.Dynamic?.Keys.ToList() ?? new List<string>(),
    };
}

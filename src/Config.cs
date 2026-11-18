using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Uex;

sealed class AesConfig
{
    /// <summary>Main key, for archives whose EncryptionKeyGuid is all zeros.</summary>
    public string Main { get; set; }
    /// <summary>Extra keys by archive key GUID (32 hex chars, as reported by `uex setup probe`).</summary>
    public Dictionary<string, string> Dynamic { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class ExportConfig
{
    public string MeshFormat { get; set; } = "ActorX";          // ActorX (psk/pskx/psa) | Gltf2 | UEFormat | USD | OBJ
    public string TextureFormat { get; set; } = "Png";          // Png | Tga | Dds | Jpeg ...
    public string MaterialDepth { get; set; } = "TopLayerOnly"; // TopLayerOnly | ... (see `uex help export`)
    public string MeshQuality { get; set; } = "Highest";
    public bool ExportMaterials { get; set; } = true;
}

sealed class GameProfile
{
    public string Name { get; set; }
    /// <summary>Folder holding .pak / .utoc / .ucas (usually &lt;Project&gt;/Content/Paks).</summary>
    public string Paks { get; set; }
    public List<string> ExtraPaks { get; set; } = new();
    /// <summary>Also mount archives in subfolders of Paks (mods, DLC, ~chunks).</summary>
    public bool Recursive { get; set; }
    /// <summary>CUE4Parse EGame name, e.g. GAME_UE5_3 or a game-specific one (`uex versions`).</summary>
    public string Version { get; set; }
    /// <summary>ETexturePlatform, default DesktopMobile.</summary>
    public string Platform { get; set; }
    public AesConfig Aes { get; set; } = new();
    /// <summary>.usmap file, or a folder (newest .usmap wins). Needed by most UE5 games (unversioned properties).</summary>
    public string Mappings { get; set; }
    public ExportConfig Export { get; set; } = new();
    public string Notes { get; set; }
}

sealed class UexConfig
{
    public int Schema { get; set; } = 1;
    public string DefaultGame { get; set; }
    /// <summary>Folder with oodle / zlib-ng / CUE4Parse-Natives / Detex for this OS. Default: &lt;config dir&gt;/natives.</summary>
    public string NativesDir { get; set; }
    public Dictionary<string, GameProfile> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

static class ConfigStore
{
    static readonly JsonSerializerSettings Json = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false } },
    };

    /// <summary>--config &gt; $UEX_CONFIG &gt; uex.config.json next to the binary (portable) &gt; per-user config dir.</summary>
    public static string ResolvePath(string cli)
    {
        if (!string.IsNullOrEmpty(cli)) return Path.GetFullPath(Paths.Expand(cli));
        var env = Environment.GetEnvironmentVariable("UEX_CONFIG");
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(Paths.Expand(env));
        var portable = Path.Combine(AppContext.BaseDirectory, "uex.config.json");
        if (File.Exists(portable)) return portable;
        return Path.Combine(Paths.UserConfigDir(), "config.json");
    }

    public static UexConfig Load(string path)
    {
        if (!File.Exists(path)) return new UexConfig();
        try
        {
            var c = JsonConvert.DeserializeObject<UexConfig>(File.ReadAllText(path), Json) ?? new UexConfig();
            c.Games = new Dictionary<string, GameProfile>(c.Games ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var g in c.Games.Values)
            {
                g.Aes ??= new AesConfig();
                g.Aes.Dynamic = new Dictionary<string, string>(g.Aes.Dynamic ?? new(), StringComparer.OrdinalIgnoreCase);
                g.Export ??= new ExportConfig();
                g.ExtraPaks ??= new List<string>();
            }
            return c;
        }
        catch (JsonException e) { throw new UexError("bad_config", $"cannot parse config {path}: {e.Message}", "fix or delete the file"); }
    }

    /// <summary>Atomic, UTF-8 without BOM.</summary>
    public static void Save(string path, UexConfig c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(c, Json), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    public static (string id, GameProfile profile) Pick(UexConfig c, string requested)
    {
        var id = requested ?? Environment.GetEnvironmentVariable("UEX_GAME") ?? c.DefaultGame;
        if (id == null)
        {
            if (c.Games.Count == 1) id = c.Games.Keys.First();
            else if (c.Games.Count == 0) throw new UexError("no_config", "no game configured", "run `uex setup detect <game folder>` then `uex games add ...` (see `uex help setup`)");
            else throw new UexError("unknown_game", "several games configured and no default", "pass --game <id> or run `uex games default <id>`", new { games = c.Games.Keys });
        }
        if (!c.Games.TryGetValue(id, out var p))
            throw new UexError("unknown_game", $"no game profile '{id}'", "run `uex games list`", new { games = c.Games.Keys });
        return (id, p);
    }

    public static string ValidateId(string id)
    {
        if (!Regex.IsMatch(id ?? "", @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$"))
            throw new UexError("bad_argument", $"invalid game id '{id}'", "use letters, digits, '-', '_' or '.' (e.g. dbd, fortnite, my-game)");
        return id;
    }
}

static class Paths
{
    public static string Expand(string p)
    {
        if (string.IsNullOrEmpty(p)) return p;
        p = p.Trim().Trim('"');
        if (p == "~" || p.StartsWith("~/") || p.StartsWith("~\\"))
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        p = Environment.ExpandEnvironmentVariables(p);                       // %VAR%
        p = Regex.Replace(p, @"\$\{(\w+)\}|\$(\w+)", m =>                   // $VAR / ${VAR}
            Environment.GetEnvironmentVariable(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) ?? m.Value);
        return p;
    }

    public static string UserConfigDir()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "uex");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdg, "uex");
    }
}

static class Versions
{
    public static string[] All => Enum.GetNames<EGame>();

    /// <summary>Accepts GAME_UE5_3, UE5_3, 5.3, ue5.3, DeadByDaylight (case-insensitive).</summary>
    public static EGame Parse(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) throw new UexError("bad_version", "no version given", "pass --version (e.g. GAME_UE5_3); `uex setup detect` suggests one");
        var s = v.Trim();
        var m = Regex.Match(s, @"^(?:GAME_)?UE?(\d)[._](\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) m = Regex.Match(s, @"^(\d)\.(\d+)$");
        if (m.Success) s = $"GAME_UE{m.Groups[1].Value}_{m.Groups[2].Value}";
        else if (!s.StartsWith("GAME_", StringComparison.OrdinalIgnoreCase)) s = "GAME_" + s;
        if (Enum.TryParse<EGame>(s, true, out var g) && Enum.GetNames<EGame>().Any(n => n.Equals(s, StringComparison.OrdinalIgnoreCase))) return g;
        var key = Norm(v);
        var near = All.Where(n => Norm(n).Contains(key) || (key.Length >= 4 && key.Contains(Norm(n)) && Norm(n).Length >= 4)).Take(15).ToList();
        throw new UexError("bad_version", $"unknown CUE4Parse version '{v}'", "run `uex versions --filter <text>` to list valid names", new { suggestions = near });
    }

    public static string Name(EGame g) => Enum.GetName(g) ?? g.ToString();

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace("game_", ""), "[^a-z0-9]", "");

    /// <summary>Game-specific EGame names whose name appears in one of the hints (project folder, exe name...).</summary>
    public static List<string> MatchGame(IEnumerable<string> hints)
    {
        var h = hints.Where(x => !string.IsNullOrEmpty(x)).Select(Norm).Distinct().ToList();
        return All.Where(n => !Regex.IsMatch(n, @"^GAME_UE\d"))
                  .Where(n => { var k = Norm(n); return k.Length >= 4 && h.Any(x => x.Contains(k) || (x.Length >= 4 && k.Contains(x))); })
                  .Distinct().OrderBy(n => n.EndsWith("_Old", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToList();
    }

    /// <summary>GAME_UEx_y if it exists, else the closest lower minor.</summary>
    public static string Generic(int major, int minor)
    {
        for (int m = minor; m >= 0; m--)
        {
            var n = $"GAME_UE{major}_{m}";
            if (All.Contains(n)) return n;
        }
        return major >= 5 ? "GAME_UE5_LATEST" : "GAME_UE4_LATEST";
    }
}

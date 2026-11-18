using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json.Linq;

namespace Uex;

static partial class Cmds
{
    /// <summary>Profile as shown to agents: everything except key values.</summary>
    static object Describe(GameProfile p) => new
    {
        name = p.Name,
        paks = p.Paks,
        paksExists = Directory.Exists(Paths.Expand(p.Paks ?? "")),
        extraPaks = p.ExtraPaks,
        recursive = p.Recursive,
        version = p.Version,
        platform = p.Platform,
        aes = Keys.Describe(p.Aes),
        mappings = p.Mappings,
        export = p.Export,
        notes = p.Notes,
    };

    static T ParseEnum<T>(string v, string opt) where T : struct, Enum =>
        Enum.TryParse<T>(v, true, out var r) && Enum.GetNames<T>().Any(n => n.Equals(v, StringComparison.OrdinalIgnoreCase)) ? r
        : throw new UexError("bad_argument", $"--{opt}: unknown value '{v}'", "valid: " + string.Join(", ", Enum.GetNames<T>()));

    /// <summary>Shared by games add/set and setup probe. Validates everything except that keys actually work (that is probe's job).</summary>
    static void ApplyProfileOptions(GameProfile p, CliArgs a, List<string> warnings, bool creating)
    {
        if (a.Opt("paks") is { } paks)
        {
            var full = Path.GetFullPath(Paths.Expand(paks));
            if (!Directory.Exists(full)) throw new UexError("paks_not_found", $"paks folder not found: {full}", "`uex setup detect <game folder>` finds it");
            p.Paks = full;
        }
        if (a.Opt("version") is { } ver) p.Version = Versions.Name(Versions.Parse(ver));
        if (a.Opt("name") is { } name) p.Name = name;
        if (a.Opt("notes") is { } notes) p.Notes = notes;
        if (a.Opt("platform") is { } plat) p.Platform = Enum.GetName(ParseEnum<ETexturePlatform>(plat, "platform"));
        if (a.Flag("recursive")) p.Recursive = true;
        if (a.Flag("no-recursive")) p.Recursive = false;
        if (a.Opts.ContainsKey("extra-paks"))
        {
            p.ExtraPaks = a.Multi("extra-paks").Select(x => Path.GetFullPath(Paths.Expand(x))).ToList();
            foreach (var x in p.ExtraPaks.Where(x => !Directory.Exists(x))) warnings.Add($"extra paks folder not found: {x}");
        }
        if (a.Opt("mappings") is { } map)
        {
            var full = Path.GetFullPath(Paths.Expand(map));
            if (!File.Exists(full) && !Directory.Exists(full)) throw new UexError("bad_argument", $"mappings not found: {full}", "give a .usmap file or a folder of .usmap (newest wins)");
            p.Mappings = full;
        }
        p.Aes ??= new AesConfig();
        if (a.Opt("aes") is { } aes) p.Aes.Main = aes.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : Keys.NormKey(aes);
        foreach (var kv in a.Multi("aes-key"))
        {
            int eq = kv.IndexOf('=');
            if (eq <= 0) throw new UexError("bad_key", "--aes-key expects <guid>=<key>", "guid from `uex setup probe` requiredKeys[].guid");
            var guid = Keys.NormGuid(kv[..eq]); var key = kv[(eq + 1)..];
            if (key.Equals("none", StringComparison.OrdinalIgnoreCase)) p.Aes.Dynamic.Remove(guid);
            else if (System.Text.RegularExpressions.Regex.IsMatch(guid, "^0+$")) p.Aes.Main = Keys.NormKey(key);
            else p.Aes.Dynamic[guid] = Keys.NormKey(key);
        }
        p.Export ??= new ExportConfig();
        if (a.Opt("mesh-format") is { } mf) p.Export.MeshFormat = Enum.GetName(ParseEnum<EMeshFormat>(mf, "mesh-format"));
        if (a.Opt("texture-format") is { } tf) p.Export.TextureFormat = Enum.GetName(ParseEnum<ETextureFormat>(tf, "texture-format"));
        if (a.Opt("material-depth") is { } md) p.Export.MaterialDepth = Enum.GetName(ParseEnum<EMaterialDepth>(md, "material-depth"));
        if (a.Opt("mesh-quality") is { } mq) p.Export.MeshQuality = Enum.GetName(ParseEnum<EMeshQuality>(mq, "mesh-quality"));
        if (a.Opt("export-materials") is { } em) p.Export.ExportMaterials = bool.TryParse(em, out var b) ? b : throw new UexError("bad_argument", "--export-materials expects true|false");

        foreach (var u in a.Multi("unset"))
            switch (u.ToLowerInvariant())
            {
                case "name": p.Name = null; break;
                case "mappings": p.Mappings = null; break;
                case "aes": p.Aes = new AesConfig(); break;
                case "platform": p.Platform = null; break;
                case "extra-paks": p.ExtraPaks = new List<string>(); break;
                case "notes": p.Notes = null; break;
                default: throw new UexError("bad_argument", $"--unset: unknown field '{u}'", "one of: name, mappings, aes, platform, extra-paks, notes");
            }
        if (creating && p.Mappings == null && p.Version != null && Versions.Parse(p.Version) >= EGame.GAME_UE5_0)
            warnings.Add("UE5 profile without --mappings: fine if the game ships versioned packages, otherwise the probe will say needs_mappings");
    }

    public static object GamesList(Ctx c, CliArgs a) => new
    {
        configPath = c.ConfigPath,
        defaultGame = c.Config.DefaultGame,
        games = c.Config.Games.Select(g => new
        {
            id = g.Key, name = g.Value.Name, version = g.Value.Version, paks = g.Value.Paks,
            paksExists = Directory.Exists(Paths.Expand(g.Value.Paks ?? "")),
            aes = Keys.Describe(g.Value.Aes), mappings = g.Value.Mappings, isDefault = string.Equals(g.Key, c.Config.DefaultGame, StringComparison.OrdinalIgnoreCase),
        }),
    };

    public static object GamesShow(Ctx c, CliArgs a)
    {
        var (id, p) = ConfigStore.Pick(c.Config, a.Pos.Count > 0 ? a.Pos[0] : a.Opt("game"));
        return new { id, isDefault = string.Equals(id, c.Config.DefaultGame, StringComparison.OrdinalIgnoreCase), profile = Describe(p) };
    }

    public static object GamesAdd(Ctx c, CliArgs a)
    {
        var id = ConfigStore.ValidateId(a.Need(0, "id"));
        if (c.Config.Games.ContainsKey(id) && !a.Flag("force")) throw new UexError("game_exists", $"profile '{id}' exists", "use `uex games set` to change it, or --force to replace it");
        if (!a.Has("paks") || !a.Has("version")) throw new UexError("usage", "games add needs --paks and --version", "`uex setup detect <game folder>` gives both");
        var p = new GameProfile();
        ApplyProfileOptions(p, a, c.Warnings, creating: true);
        p.Name ??= id;
        c.Config.Games[id] = p;
        if (a.Flag("default") || c.Config.DefaultGame == null) c.Config.DefaultGame = id;
        c.SaveConfig();
        return new { id, configPath = c.ConfigPath, isDefault = c.Config.DefaultGame == id, profile = Describe(p), nextSteps = new[] { $"uex setup probe -g {id}" } };
    }

    public static object GamesSet(Ctx c, CliArgs a)
    {
        var id = a.Need(0, "id");
        if (!c.Config.Games.TryGetValue(id, out var p)) throw new UexError("unknown_game", $"no game profile '{id}'", "run `uex games list`");
        ApplyProfileOptions(p, a, c.Warnings, creating: false);
        c.SaveConfig();
        return new { id, profile = Describe(p), nextSteps = new[] { $"uex setup probe -g {id}" } };
    }

    public static object GamesRemove(Ctx c, CliArgs a)
    {
        var id = a.Need(0, "id");
        if (!c.Config.Games.Remove(id)) throw new UexError("unknown_game", $"no game profile '{id}'", "run `uex games list`");
        if (string.Equals(c.Config.DefaultGame, id, StringComparison.OrdinalIgnoreCase)) c.Config.DefaultGame = c.Config.Games.Keys.FirstOrDefault();
        c.SaveConfig();
        return new { removed = id, defaultGame = c.Config.DefaultGame };
    }

    public static object GamesDefault(Ctx c, CliArgs a)
    {
        var id = a.Need(0, "id");
        if (!c.Config.Games.ContainsKey(id)) throw new UexError("unknown_game", $"no game profile '{id}'", "run `uex games list`");
        c.Config.DefaultGame = c.Config.Games.Keys.First(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
        c.SaveConfig();
        return new { defaultGame = c.Config.DefaultGame };
    }

    /// <summary>FModel AppSettings.json: PerDirectory[gameDir] = {GameName, UeVersion (EGame number), TexturePlatform, AesKeys{mainKey, dynamicKeys[]}, Endpoints[]}.</summary>
    public static object ImportFModel(Ctx c, CliArgs a)
    {
        var path = a.Opt("settings") is { } sp ? Path.GetFullPath(Paths.Expand(sp))
                 : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings.json");
        if (!File.Exists(path)) throw new UexError("bad_argument", $"FModel settings not found: {path}", "pass --settings <AppSettings.json>");
        var s = JObject.Parse(File.ReadAllText(path));
        var output = (string)s["OutputDirectory"];
        var dataDir = output == null ? null : Path.Combine(output, ".data");
        var imported = new List<object>();
        var per = s["PerDirectory"] as JObject ?? new JObject();
        foreach (var prop in per.Properties())
        {
            var d = prop.Value as JObject; if (d == null) continue;
            var gameDir = (string)d["GameDirectory"] ?? prop.Name;
            var gameName = (string)d["GameName"];
            var id = a.Opt("id") is { } forced && per.Count == 1 ? forced
                   : System.Text.RegularExpressions.Regex.Replace((gameName ?? Path.GetFileName(gameDir.TrimEnd('\\', '/'))).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (c.Config.Games.ContainsKey(id) && !a.Flag("force")) { imported.Add(new { id, skipped = "exists (use --force)" }); continue; }

            var p = new GameProfile { Name = gameName, Paks = gameDir };
            var raw = (long?)d["UeVersion"] ?? 0;
            p.Version = Enum.IsDefined(typeof(EGame), (uint)raw) ? Versions.Name((EGame)(uint)raw) : "GAME_UE5_LATEST";
            var tp = (int?)d["TexturePlatform"] ?? 0;
            if (tp != 0 && Enum.IsDefined(typeof(ETexturePlatform), tp)) p.Platform = Enum.GetName(typeof(ETexturePlatform), tp);
            var main = (string)d["AesKeys"]?["mainKey"];
            if (!string.IsNullOrWhiteSpace(main)) p.Aes.Main = Keys.NormKey(main);
            foreach (var dk in d["AesKeys"]?["dynamicKeys"] as JArray ?? new JArray())
            {
                var g = (string)(dk["guid"] ?? dk["Guid"]); var k = (string)(dk["key"] ?? dk["Key"]);
                if (!string.IsNullOrWhiteSpace(g) && !string.IsNullOrWhiteSpace(k)) p.Aes.Dynamic[Keys.NormGuid(g)] = Keys.NormKey(k);
            }
            var usesMappings = (d["Endpoints"] as JArray)?.Skip(1).Any(e => (bool?)e["IsValid"] == true) == true;
            var mapDir = dataDir == null ? null : Path.Combine(dataDir, "mappings");
            if (usesMappings && mapDir != null && Directory.Exists(mapDir)) p.Mappings = mapDir;
            c.Config.Games[id] = p;
            c.Config.DefaultGame ??= id;
            imported.Add(new { id, profile = Describe(p) });
            if (p.Version.EndsWith("_Old")) c.Warnings.Add($"{id}: FModel uses {p.Version}; if probe fails try the version without _Old");
        }
        c.SaveConfig();
        if (dataDir != null && Directory.Exists(dataDir)) c.Warnings.Add($"FModel natives folder: run `uex setup natives --from \"{dataDir}\"`");
        return new { settings = path, imported, nextSteps = new[] { "uex setup probe -g <id>" } };
    }
}

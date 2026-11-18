// uex - Unreal Engine asset extractor CLI built on CUE4Parse, made to be driven by AI agents.
// Contract: one JSON envelope on stdout per command ({ok, command, game, data|error, warnings}),
// logs on stderr only, stable error codes and exit codes. `uex help` returns the full command schema.
using System.Text;
using Serilog;
using Serilog.Events;

namespace Uex;

sealed class Ctx
{
    public string ConfigPath;
    public UexConfig Config;
    public string GameOverride;
    public List<string> Warnings = new();
    public bool Partial;
    public bool InBatch;
    Session _session;

    public string NativesDir => Natives.Dir(Config, ConfigPath);
    public string GameId => _session?.GameId;
    public bool Mounted => _session != null;

    public Session Session
    {
        get
        {
            if (_session != null) return _session;
            var (id, prof) = ConfigStore.Pick(Config, GameOverride);
            _session = Session.Open(id, prof, NativesDir, Warnings);
            return _session;
        }
    }

    public void SaveConfig() => ConfigStore.Save(ConfigPath, Config);
}

sealed record Cmd(string Name, string Summary, string Usage, string[] Options, bool Mounts, Func<Ctx, CliArgs, object> Run, string Example = null);

static partial class Program
{
    public const string Version = "0.1.0";
    static readonly string[] GlobalOptions = { "game", "config", "pretty", "verbose", "help" };

    static int Main(string[] argv)
    {
        Output.Stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);   // anything a library prints goes to stderr, never into our JSON
        Console.OutputEncoding = new UTF8Encoding(false);

        CliArgs a;
        try { a = CliArgs.Parse(argv); }
        catch (UexError e) { Output.Write(Output.Fail(null, null, e, null)); return UexError.ExitFor(e.Code); }

        Output.Pretty = a.Flag("pretty");
        Log.Logger = a.Flag("verbose")
            ? new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose).CreateLogger()
            : Serilog.Core.Logger.None;

        var ctx = new Ctx { GameOverride = a.Opt("game") };
        try
        {
            ctx.ConfigPath = ConfigStore.ResolvePath(a.Opt("config"));
            ctx.Config = ConfigStore.Load(ctx.ConfigPath);
        }
        catch (UexError e) { Output.Write(Output.Fail(null, null, e, null)); return UexError.ExitFor(e.Code); }

        return Run(ctx, a);
    }

    public static int Run(Ctx ctx, CliArgs a)
    {
        ctx.Warnings = new List<string>();
        ctx.Partial = false;
        Cmd cmd = null;
        try
        {
            if (a.Pos.Count == 0) { Output.Write(Output.Ok("help", null, HelpAll(), null, false), ctx.InBatch); return 0; }
            cmd = Resolve(a);
            if (a.Flag("help")) { Output.Write(Output.Ok("help", null, Spec(cmd), null, false), ctx.InBatch); return 0; }
            var allowed = new HashSet<string>(cmd.Options.Concat(GlobalOptions), StringComparer.OrdinalIgnoreCase);
            var unknown = a.Opts.Keys.Concat(a.Flags).FirstOrDefault(o => !allowed.Contains(o));
            if (unknown != null) throw new UexError("usage", $"unknown option --{unknown} for `{cmd.Name}`", $"valid: {string.Join(", ", cmd.Options.Select(o => "--" + o))}");

            var data = cmd.Run(ctx, a);
            Output.Write(Output.Ok(cmd.Name, ctx.GameId, data, ctx.Warnings, ctx.Partial), ctx.InBatch);
            return ctx.Partial ? 1 : 0;
        }
        catch (UexError e)
        {
            Output.Write(Output.Fail(cmd?.Name, ctx.GameId, e, ctx.Warnings), ctx.InBatch);
            return UexError.ExitFor(e.Code);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Output.Write(Output.Fail(cmd?.Name, ctx.GameId, new UexError("internal", Output.Unwrap(e), "rerun with --verbose; stack trace is on stderr"), ctx.Warnings), ctx.InBatch);
            return 10;
        }
    }

    static Cmd Resolve(CliArgs a)
    {
        Cmd c = null;
        if (a.Pos.Count >= 2) c = Commands.FirstOrDefault(x => x.Name.Equals(a.Pos[0] + " " + a.Pos[1], StringComparison.OrdinalIgnoreCase));
        if (c != null) { a.Pos.RemoveRange(0, 2); return c; }
        c = Commands.FirstOrDefault(x => x.Name.Equals(a.Pos[0], StringComparison.OrdinalIgnoreCase));
        if (c != null) { a.Pos.RemoveAt(0); return c; }
        var group = Commands.Where(x => x.Name.StartsWith(a.Pos[0] + " ", StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToList();
        if (group.Count > 0) throw new UexError("usage", $"`{a.Pos[0]}` needs a subcommand", "one of: " + string.Join(", ", group));
        throw new UexError("unknown_command", $"unknown command '{a.Pos[0]}'", "run `uex help`", new { commands = Commands.Select(x => x.Name) });
    }

    // ---- self-description ---------------------------------------------------------------------

    static object Spec(Cmd c) => new { name = c.Name, summary = c.Summary, usage = c.Usage, options = c.Options, mountsGame = c.Mounts, example = c.Example };

    static object HelpAll() => new
    {
        tool = "uex",
        version = Version,
        description = "Unreal Engine archive explorer/extractor (CUE4Parse). Every command prints exactly one JSON envelope on stdout: "
                    + "{ok:true, command, game, data, warnings?, partial?} or {ok:false, command, error:{code, message, hint, details}}. Logs go to stderr.",
        globalOptions = new Dictionary<string, string>
        {
            ["--game, -g <id>"] = "game profile to use (else $UEX_GAME, else defaultGame, else the only profile)",
            ["--config <file>"] = "config file (else $UEX_CONFIG, else uex.config.json next to the binary, else the per-user config)",
            ["--pretty"] = "indent the JSON",
            ["--verbose"] = "CUE4Parse logs on stderr",
            ["--help"] = "spec of one command",
        },
        setupWorkflow = new[]
        {
            "1. uex doctor                                  -> config path, natives present for this OS, games configured",
            "2. uex setup natives --from <dir>              -> copy oodle/zlib/ACL natives (e.g. FModel Output/.data); or --download (oodle+zlib)",
            "3. uex setup detect <game install folder>      -> finds the Paks folder, engine version from the executable, version candidates",
            "4. uex setup probe --paks <dir> --version <v>  -> mounts without saving: locked archives + key GUIDs, sample load test, verdict",
            "5. add keys/mappings and re-probe until verdict is ok (--aes <key>, --aes-key <guid>=<key>, --mappings <file.usmap>)",
            "6. uex setup probe ... --save <id>             -> or `uex games add <id> ...`; then `uex info -g <id>`",
            "AES keys and .usmap mappings are NOT discoverable by uex: ask the user or use a source they trust. Keys are stored in the config and never printed.",
        },
        exitCodes = new Dictionary<string, string>
        {
            ["0"] = "ok", ["1"] = "partial (some items failed, see data)", ["2"] = "usage", ["3"] = "config/version/key", ["4"] = "mount / paks folder",
            ["5"] = "not_found", ["6"] = "missing_native", ["7"] = "output_too_large", ["8"] = "load_failed", ["10"] = "internal",
        },
        commands = Commands.Select(Spec),
    };

    static readonly string[] PageOpts = { "offset", "limit" };
    static readonly string[] ProfileOpts = { "paks", "version", "name", "aes", "aes-key", "mappings", "recursive", "no-recursive", "extra-paks", "platform", "notes",
                                             "mesh-format", "texture-format", "material-depth", "mesh-quality", "export-materials" };

    static readonly Cmd[] Commands =
    {
        new("help", "full machine-readable command list, or the spec of one command", "uex help [<command>]", Array.Empty<string>(), false, (c, a) => a.Pos.Count == 0 ? HelpAll() : Spec(Resolve(a))),
        new("doctor", "environment report: OS, config file, natives found, configured games", "uex doctor", Array.Empty<string>(), false, Cmds.Doctor),
        new("versions", "list CUE4Parse game/engine version names usable in --version", "uex versions [--filter <regex>]", new[] { "filter" }, false, Cmds.ListVersions, "uex versions --filter ue5"),

        new("setup detect", "inspect a game install: Paks folder(s), engine version from the executable, version candidates, next commands", "uex setup detect <game folder> [--depth 6]",
            new[] { "depth" }, false, Cmds.Detect, "uex setup detect \"C:/Program Files (x86)/Steam/steamapps/common/MyGame\""),
        new("setup probe", "mount with the given (or saved) settings WITHOUT saving: locked archives + key GUIDs, mappings need, sample load test, verdict. --save <id> stores it",
            "uex setup probe (--game <id> | --paks <dir> --version <v>) [--aes <key>] [--aes-key <guid>=<key>]... [--mappings <usmap>] [--sample 12] [--convert 2 | --no-convert] [--save <id> [--force] [--default]]",
            ProfileOpts.Concat(new[] { "sample", "convert", "no-convert", "save", "force", "default" }).ToArray(), true, Cmds.Probe, "uex setup probe --paks D:/Game/Content/Paks --version GAME_UE5_3 --aes 0x0123..."),
        new("setup natives", "show natives for this OS; copy them from folders (--from, searched 3 levels deep) or --download oodle+zlib-ng",
            "uex setup natives [--from <dir>]... [--download]", new[] { "from", "download" }, false, Cmds.SetupNatives, "uex setup natives --from \"<FModel>/Output/.data\""),

        new("games list", "configured game profiles (keys never shown)", "uex games list", Array.Empty<string>(), false, Cmds.GamesList),
        new("games show", "one profile (keys masked)", "uex games show [<id>]", Array.Empty<string>(), false, Cmds.GamesShow),
        new("games add", "create a profile (validated, not mounted: run `uex setup probe -g <id>` after)", "uex games add <id> --paks <dir> --version <v> [profile options] [--default] [--force]",
            ProfileOpts.Concat(new[] { "default", "force" }).ToArray(), false, Cmds.GamesAdd, "uex games add mygame --paks D:/MyGame/Content/Paks --version GAME_UE5_3 --default"),
        new("games set", "change fields of a profile; --aes none / --aes-key <guid>=none remove keys; --unset <field> clears name|mappings|aes|platform|extra-paks|notes",
            "uex games set <id> [profile options] [--unset <field>]...", ProfileOpts.Concat(new[] { "unset" }).ToArray(), false, Cmds.GamesSet, "uex games set mygame --mappings D:/maps/MyGame.usmap"),
        new("games remove", "delete a profile", "uex games remove <id>", Array.Empty<string>(), false, Cmds.GamesRemove),
        new("games default", "set the default profile", "uex games default <id>", Array.Empty<string>(), false, Cmds.GamesDefault),
        new("games import-fmodel", "create profiles from FModel's AppSettings.json (game dir, version, AES keys, mappings folder)",
            "uex games import-fmodel [--settings <AppSettings.json>] [--id <id>] [--force]", new[] { "settings", "id", "force" }, false, Cmds.ImportFModel),

        new("info", "mount the game and summarise: files, archives mounted/locked, mappings, natives", "uex info [-g <id>]", Array.Empty<string>(), true, Cmds.Info),
        new("ls", "list a virtual folder (sub-folders with file counts, then packages; --all for every file type)", "uex ls [<folder>] [--all] [--offset 0] [--limit 500]",
            PageOpts.Concat(new[] { "all" }).ToArray(), true, Cmds.Ls, "uex ls MyGame/Content/Meshes"),
        new("search", "regex over virtual paths (case-insensitive). Packages only unless --ext or --all", "uex search <regex> [--ext uasset|umap|bnk|...] [--all] [--offset 0] [--limit 200]",
            PageOpts.Concat(new[] { "ext", "all" }).ToArray(), true, Cmds.Search, "uex search \"SM_.*Chair\" --limit 50"),
        new("types", "exports of a package: class + name (decide what to do with it)", "uex types <package>", Array.Empty<string>(), true, Cmds.Types, "uex types /Game/Meshes/SM_Chair"),
        new("props", "properties of a package as JSON (all exports, or --export <name> / --class <class>). Big results must go to --out",
            "uex props <package> [--export <name>] [--class <class>] [--out <file.json>] [--max-bytes 200000]", new[] { "export", "class", "out", "max-bytes" }, true, Cmds.Props),
        new("raw", "copy raw bytes of archive files (.bnk, .wem, .ini, .uasset...) to a folder", "uex raw <path>... [--out <dir>] [--keep-tree]",
            new[] { "out", "keep-tree" }, true, Cmds.Raw),
        new("export", "convert assets: StaticMesh/SkeletalMesh (+materials+textures), Texture, AnimSequence, Material -> files; other packages -> JSON. Paths from args and/or --list <file|->",
            "uex export <package>... [--list <file|->] [--out <dir>] [--mesh-format ActorX|Gltf2|UEFormat|USD|OBJ] [--texture-format Png|Tga|...] [--material-depth ...] [--no-materials]",
            new[] { "list", "out", "mesh-format", "texture-format", "material-depth", "mesh-quality", "no-materials" }, true, Cmds.Export, "uex export /Game/Meshes/SM_Chair --out ./out"),
        new("map", "export a whole level: World .umap -> USD scene (CUE4Parse only supports USD for worlds); DBD PaperTileMap also gltf/actorx/ueformat", "uex map <package> [--out <dir>] [--format usd|actorx|gltf|ueformat] [--seed 0]",
            new[] { "out", "format", "seed" }, true, Cmds.Map),
        new("batch", "run many commands on ONE mount (fast). Reads command lines from a file or stdin ('-'); prints one JSON envelope per line (NDJSON). '#' lines ignored",
            "uex batch <file|-> [-g <id>] [--stop-on-error]", new[] { "stop-on-error" }, true, Cmds.Batch, "printf 'types /Game/A\\nexport /Game/A --out x\\n' | uex batch - -g mygame"),
    };
}

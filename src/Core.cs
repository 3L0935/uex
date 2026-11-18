using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Uex;

/// <summary>Error with a stable machine code. Exit code is derived from the code.</summary>
sealed class UexError : Exception
{
    public string Code { get; }
    public string Hint { get; }
    public object Details { get; }
    public UexError(string code, string message, string hint = null, object details = null) : base(message) { Code = code; Hint = hint; Details = details; }

    public static int ExitFor(string code) => code switch
    {
        "usage" or "unknown_command" or "bad_argument" => 2,
        "no_config" or "unknown_game" or "bad_version" or "bad_key" or "bad_config" or "game_exists" => 3,
        "mount_failed" or "paks_not_found" => 4,
        "not_found" => 5,
        "load_failed" => 8,
        "missing_native" => 6,
        "output_too_large" => 7,
        _ => 10,
    };
}

/// <summary>Minimal argument parser: positionals, --opt value, --opt=value, boolean flags, repeated options.</summary>
sealed class CliArgs
{
    static readonly HashSet<string> BoolFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "pretty", "verbose", "help", "recursive", "no-recursive", "force", "all", "download", "default", "keep-tree", "stop-on-error", "no-materials", "no-convert",
    };
    static readonly Dictionary<string, string> Short = new() { ["-g"] = "game", ["-o"] = "out", ["-h"] = "help", ["-n"] = "limit" };

    public List<string> Pos { get; } = new();
    public Dictionary<string, List<string>> Opts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(IEnumerable<string> argv)
    {
        var a = new CliArgs();
        var list = argv.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i];
            string name = null, val = null;
            if (Short.TryGetValue(t, out var sn)) name = sn;
            else if (t.StartsWith("--") && t.Length > 2)
            {
                name = t[2..];
                int eq = name.IndexOf('=');
                if (eq >= 0) { val = name[(eq + 1)..]; name = name[..eq]; }
            }
            if (name == null) { a.Pos.Add(t); continue; }
            if (BoolFlags.Contains(name) && val == null) { a.Flags.Add(name); continue; }
            if (val == null)
            {
                if (i + 1 >= list.Count) throw new UexError("usage", $"option --{name} expects a value");
                val = list[++i];
            }
            if (!a.Opts.TryGetValue(name, out var l)) a.Opts[name] = l = new List<string>();
            l.Add(val);
        }
        return a;
    }

    public string Opt(string n, string def = null) => Opts.TryGetValue(n, out var l) ? l[^1] : def;
    public List<string> Multi(string n) => Opts.TryGetValue(n, out var l) ? l : new List<string>();
    public bool Has(string n) => Opts.ContainsKey(n) || Flags.Contains(n);
    public bool Flag(string n) => Flags.Contains(n);
    public int Int(string n, int def)
    {
        var v = Opt(n);
        if (v == null) return def;
        if (!int.TryParse(v, out var i) || i < 0) throw new UexError("bad_argument", $"--{n} expects a non-negative integer, got '{v}'");
        return i;
    }
    public string Need(int index, string what)
    {
        if (index >= Pos.Count) throw new UexError("usage", $"missing argument <{what}>", "run `uex help <command>` for the syntax");
        return Pos[index];
    }

    /// <summary>Split one batch line into tokens (double quotes group, backslash is literal so Windows paths survive).</summary>
    public static List<string> Tokenize(string line)
    {
        var r = new List<string>(); var sb = new StringBuilder(); bool q = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { q = !q; any = true; continue; }
            if (!q && char.IsWhiteSpace(c)) { if (sb.Length > 0 || any) { r.Add(sb.ToString()); sb.Clear(); any = false; } continue; }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) r.Add(sb.ToString());
        return r;
    }
}

/// <summary>stdout carries exactly one JSON document per command (NDJSON in batch). Everything else goes to stderr.</summary>
static class Output
{
    public static TextWriter Stdout;
    public static bool Pretty;

    public static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        // camelCase for our own record/class properties, but never rewrite dictionary keys (game ids, GUIDs, paths)
        ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = false } },
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
    };

    public static void Write(object envelope, bool forceCompact = false)
    {
        var json = JsonConvert.SerializeObject(envelope, Pretty && !forceCompact ? Formatting.Indented : Formatting.None, Settings);
        Stdout.WriteLine(json);
        Stdout.Flush();
    }

    public static object Ok(string command, string game, object data, List<string> warnings, bool partial)
    {
        var d = new Dictionary<string, object> { ["ok"] = true, ["command"] = command };
        if (game != null) d["game"] = game;
        if (partial) d["partial"] = true;
        d["data"] = data;
        if (warnings is { Count: > 0 }) d["warnings"] = warnings;
        return d;
    }

    public static object Fail(string command, string game, UexError e, List<string> warnings)
    {
        var err = new Dictionary<string, object> { ["code"] = e.Code, ["message"] = e.Message };
        if (e.Hint != null) err["hint"] = e.Hint;
        if (e.Details != null) err["details"] = e.Details;
        var d = new Dictionary<string, object> { ["ok"] = false, ["command"] = command };
        if (game != null) d["game"] = game;
        d["error"] = err;
        if (warnings is { Count: > 0 }) d["warnings"] = warnings;
        return d;
    }

    public static string Unwrap(Exception e)
    {
        while (e is AggregateException { InnerException: not null } a) e = a.InnerException;
        if (e is System.Reflection.TargetInvocationException { InnerException: not null } t) e = t.InnerException;
        return e is ArgumentException ae && string.IsNullOrEmpty(ae.Message) ? ae.ParamName : e.Message;
    }

    /// <summary>CUE4Parse objects -> JSON using their own converters (Newtonsoft default, not camelCase: keep UE names).</summary>
    public static string UeJson(object o, bool indented = true) => JsonConvert.SerializeObject(o, indented ? Formatting.Indented : Formatting.None);
}

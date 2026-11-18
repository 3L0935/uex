using System.Runtime.InteropServices;
using CUE4Parse.Compression;
using CUE4Parse_Conversion.Textures.BC;

namespace Uex;

/// <summary>
/// Native libraries CUE4Parse can use. None is bundled: they come from `uex setup natives --from &lt;dir&gt;`
/// (e.g. FModel's Output/.data) or `--download` (Oodle + zlib-ng only, from CUE4Parse's own URLs).
/// </summary>
static class Natives
{
    public sealed record Lib(string Id, string Purpose, string[] Windows, string[] Linux, string[] Mac);

    public static readonly Lib[] Libs =
    {
        new("oodle", "decompress Oodle archives (most UE4.27+/UE5 games)",
            new[] { OodleHelper.OODLE_NAME_CURRENT, OodleHelper.OODLE_NAME_OLD, "oodle-data-shared.dll", "oo2core_9_win64.dll" },
            new[] { OodleHelper.OODLE_NAME_LINUX, "liboo2corelinux64.so.9", "liboo2corelinux64.so" }, Array.Empty<string>()),
        new("zlib", "decompress zlib archives",
            new[] { ZlibHelper.DLL_NAME }, new[] { ZlibHelper.DLL_NAME_LINUX }, Array.Empty<string>()),
        new("acl", "decode ACL-compressed AnimSequence (animation export)",
            new[] { "CUE4Parse-Natives.dll" }, new[] { "libCUE4Parse-Natives.so", "CUE4Parse-Natives.so" }, new[] { "libCUE4Parse-Natives.dylib" }),
        new("detex", "extra texture formats (fallback decoder)",
            new[] { DetexHelper.DLL_NAME }, new[] { "libdetex.so" }, Array.Empty<string>()),
    };

    public static string[] NamesForOs(Lib l) => OperatingSystem.IsWindows() ? l.Windows : OperatingSystem.IsLinux() ? l.Linux : l.Mac;

    public static string Dir(UexConfig c, string configPath) =>
        !string.IsNullOrEmpty(c.NativesDir) ? Path.GetFullPath(Paths.Expand(c.NativesDir)) : Path.Combine(Path.GetDirectoryName(configPath)!, "natives");

    public static string Find(Lib l, string nativesDir)
    {
        foreach (var dir in new[] { nativesDir, AppContext.BaseDirectory })
            foreach (var n in NamesForOs(l).Distinct())
            {
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(n)) continue;
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    public static List<object> Status(string nativesDir) => Libs.Select(l =>
    {
        var p = Find(l, nativesDir);
        return (object)new { id = l.Id, found = p != null, path = p, purpose = l.Purpose, expectedNames = NamesForOs(l).Distinct() };
    }).ToList();

    static bool _aclHooked;
    static string _aclPath;

    /// <summary>Load what is present; report what is missing as warnings (never fatal at mount).</summary>
    public static Dictionary<string, bool> Init(string nativesDir, List<string> warnings)
    {
        var state = new Dictionary<string, bool>();
        foreach (var l in Libs)
        {
            var p = Find(l, nativesDir);
            state[l.Id] = p != null;
            if (p == null) continue;
            try
            {
                switch (l.Id)
                {
                    case "oodle": if (OodleHelper.Instance == null) OodleHelper.Initialize(p); break;
                    case "zlib": if (ZlibHelper.Instance == null) ZlibHelper.Initialize(p); break;
                    case "detex": DetexHelper.Initialize(p); break;
                    case "acl": HookAcl(p); break;
                }
            }
            catch (Exception e) { state[l.Id] = false; warnings.Add($"native '{l.Id}' found at {p} but failed to load: {Output.Unwrap(e)}"); }
        }
        return state;
    }

    /// <summary>CUE4Parse P/Invokes "CUE4Parse-Natives"; point it at our natives dir.</summary>
    static void HookAcl(string path)
    {
        _aclPath = path;
        if (_aclHooked) return;
        NativeLibrary.SetDllImportResolver(typeof(CUE4Parse.ACL.ACLNative).Assembly, (name, asm, search) =>
            _aclPath != null && name.Contains("CUE4Parse-Natives", StringComparison.OrdinalIgnoreCase) && NativeLibrary.TryLoad(_aclPath, out var h) ? h : IntPtr.Zero);
        _aclHooked = true;
    }
}

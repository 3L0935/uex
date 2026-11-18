---
name: uex
description: Install and drive uex, a JSON-first CLI that browses and extracts Unreal Engine game archives (.pak / .utoc / .ucas) for any UE4/UE5 game - static/skeletal meshes, textures, materials, animations, raw files (Wwise .bnk, .ini), package properties and whole levels. Use when the user wants to inspect or extract assets from an Unreal Engine game, set up a new game (paks folder, AES key, .usmap mappings, CUE4Parse version), or asks for something FModel / CUE4Parse / UModel would do, from a script or an agent.
---

# uex - Unreal Engine extraction for agents

uex wraps [CUE4Parse](https://github.com/FabianFG/CUE4Parse) (the library behind FModel) in a CLI made for programs:
**one JSON envelope on stdout per command, logs on stderr, stable error codes, stable exit codes.**
Never parse its stderr; always parse stdout.

## 1. Install (once)

Check first: `uex help` (or `./uex help`). If it prints JSON, skip this section.

Pick the release asset for the OS (self-contained, no .NET needed):

| OS | asset |
|---|---|
| Windows x64 | `uex-win-x64.zip` |
| Linux x64 | `uex-linux-x64.tar.gz` |

```bash
# Linux
mkdir -p ~/.local/bin && cd /tmp
curl -fsSL -o uex.tgz https://github.com/3L0935/uex/releases/latest/download/uex-linux-x64.tar.gz
tar -xzf uex.tgz -C ~/.local/bin && chmod +x ~/.local/bin/uex && uex doctor
```

```powershell
# Windows (PowerShell)
$d = "$env:LOCALAPPDATA\uex"; New-Item -ItemType Directory -Force $d | Out-Null
Invoke-WebRequest https://github.com/3L0935/uex/releases/latest/download/uex-win-x64.zip -OutFile "$env:TEMP\uex.zip"
Expand-Archive "$env:TEMP\uex.zip" $d -Force; & "$d\uex.exe" doctor
```

From source instead (needs the .NET 10 SDK): `git clone https://github.com/3L0935/uex && cd uex && dotnet publish -c Release -r <win-x64|linux-x64> -o out`.

Ask the user before installing anything system-wide. Downloading the release into a user folder is fine once they asked for uex.

## 2. The contract

```jsonc
{"ok":true,"command":"search","game":"mygame","data":{...},"warnings":["..."],"partial":true}
{"ok":false,"command":"export","error":{"code":"not_found","message":"...","hint":"...","details":{"didYouMean":["..."]}}}
```

| exit | meaning | error codes |
|---|---|---|
| 0 | ok | |
| 1 | partial: some items failed, read `data.items[].ok` | |
| 2 | usage | `usage`, `unknown_command`, `bad_argument` |
| 3 | configuration | `no_config`, `unknown_game`, `bad_version`, `bad_key`, `bad_config`, `game_exists` |
| 4 | mount | `mount_failed`, `paks_not_found` |
| 5 | not found | `not_found` (check `details.didYouMean`) |
| 6 | native library missing | `missing_native` |
| 7 | result too big for stdout | `output_too_large` (use `--out`) |
| 8 | package failed to parse | `load_failed` (version / mappings) |

Self-discovery: `uex help` gives every command with usage and options; `uex help <command>` gives one.
Always read `error.hint` and `warnings` - they say what to do next.

## 3. Configure a new game

Follow this loop. Each step's JSON tells you the next one (`data.nextSteps`, `data.hints`).

1. `uex doctor`: shows the config path, the natives found for this OS, and the configured games.
2. Natives. If oodle is missing:
   - copy from a folder that has them: `uex setup natives --from <dir>` (FModel's `Output/.data` holds oodle, zlib and Detex; FModel's `%TEMP%/.net/FModel/<hash>/` holds `CUE4Parse-Natives.dll`);
   - or `uex setup natives --download` (oodle + zlib-ng only).
   - ACL (`CUE4Parse-Natives`) is needed only for animations. On Linux it has to be built from the CUE4Parse repo.
3. `uex setup detect "<game install folder>"`: returns the Paks folder, the engine version read from the executable, `recommendedVersion` (a game-specific CUE4Parse version beats a generic `GAME_UE5_x`), and the exact probe command.
4. `uex setup probe --paks "<Paks>" --version <v>`: mounts without saving. Act on `data.verdict`:

| verdict | do |
|---|---|
| `ok` / `ok_with_errors` | save it (step 5) |
| `needs_aes` | **ask the user** for the AES key(s) of `data.requiredKeys[].guid`. Add `--aes <key>` for guid `000…0`, `--aes-key <guid>=<key>` for others, then probe again |
| `partial_aes` | usable; extra keys only unlock the listed archives (DLC/optional) |
| `needs_mappings` | **ask the user** for a `.usmap` matching the game's current version; add `--mappings <file or folder>` |
| `load_errors` | try another `--version` (`uex versions --filter <game or ue5>`), or a newer .usmap |
| `no_packages` | wrong folder, try `--recursive`, or natives missing |

5. Add the same probe arguments plus `--save <id> --default`, or run `uex games add <id> --paks … --version …`.
6. `uex info`: file count, locked archives, mappings in use.

Shortcut: if FModel is already configured on the machine, run `uex games import-fmodel` to import the game folder, version, keys and mappings folder.

**Keys rules.** uex cannot discover AES keys or mappings. Get them from the user or from a source they point you to. Never echo a key back in chat, a commit, or a log. The config stores keys; uex never prints them (`aes: {main: true}`).

## 4. Everyday use

```bash
uex search "SM_.*Chair" --limit 50              # regex over virtual paths; packages only unless --ext/--all
uex ls MyGame/Content/Meshes                    # folders with file counts, then packages (paged: --offset/--limit)
uex types /Game/Meshes/SM_Chair                 # export classes: decide what the package is
uex props /Game/Data/DT_Items --out items.json  # properties -> file (stdout is capped, see output_too_large)
uex export /Game/Meshes/SM_Chair --out out      # mesh (+materials json +textures png), texture, anim (.psa), material
uex export --list paths.txt --out out           # one path per line; '-' = stdin
uex raw MyGame/Content/WwiseAudio/Cooked/X.bnk --out raw # raw bytes of any archive file
uex map /Game/Maps/Level01 --format gltf --out level     # whole level as a scene
```

Path forms accepted: `MyGame/Content/X.uasset` (mount path, exactly as `search` prints it), `/Game/X`, `/Game/X.X`, `Class'/Game/X.X'`, `/Game/X.X:Sub`.

**Batch for anything beyond 2–3 calls.** Mounting costs seconds; a batch mounts once:
```bash
printf 'types /Game/A\nexport /Game/A --out out\nprops /Game/B --out b.json\n' | uex batch -
```
It outputs one compact JSON per line (NDJSON). The last line is `{"command":"batch","data":{"summary":true,"lines":N,"ok":…,"failed":…}}`.

Export defaults come from the profile (`ActorX` psk/pskx/psa, `Png`, `TopLayerOnly` materials). Override per call with `--mesh-format Gltf2|UEFormat|USD|OBJ`, `--texture-format`, `--material-depth`, `--no-materials`, or per game with `uex games set <id> --mesh-format …`.

## 5. Pitfalls

- **Git Bash on Windows** rewrites `/Game/...` arguments into `C:/Program Files/Git/Game/...`. uex undoes that, but prefer mount paths as `search` prints them (`MyGame/Content/...`) or set `MSYS_NO_PATHCONV=1`.
- **Never dump big props to stdout.** `props` refuses results over 200 KB with `output_too_large`. Use `--out` and then grep the file, or narrow with `--export <name>` / `--class <class>`.
- **Versions ending in `_Old`** are for older builds of that game. Prefer the plain name unless the probe fails.
- **Mappings go stale.** A UE5 game update usually needs a new `.usmap`. When previously working packages start failing with `load_failed`, ask the user for a fresh one.
- **Locked archives are invisible.** If `search` finds nothing that should exist, check `uex info` → `archives.locked`.
- **Animations** need the ACL native. Without it, `export` warns and the `.psa` fails.
- uex only reads files on disk. It never touches a running game, anti-cheat, or online services. Remind the user that ripped assets stay under the game's license.

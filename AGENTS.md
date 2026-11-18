# Notes for AI agents

- **Using uex** (install, game setup, commands, error codes): read [`skills/uex/SKILL.md`](skills/uex/SKILL.md).
- **Working on uex itself**: .NET 10, sources in `src/`. Build with `dotnet build -c Release`. Every command returns exactly one JSON envelope on stdout (`Output.Ok` / `Output.Fail` in `src/Core.cs`), and nothing else may write to stdout (`Console.Out` is redirected to stderr in `Main`). New commands are registered in the `Commands` table in `src/Program.cs`, with their allowed options; unknown options are rejected.
- Never commit a config file, AES keys, `.usmap` files or native binaries (oodle, zlib-ng, CUE4Parse-Natives, Detex).

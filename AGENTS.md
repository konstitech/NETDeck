# Repository Guidelines

## Project Overview

.NETDeck is a planned Terminal.Gui command center for .NET development and Zed. The current product is a terminal lifecycle and quit surface; functional tabs and Zed integration are not implemented. Treat `README.md` as the current status and `Projektplan.NETDeck.v1.0-Lean-MVP.md` as future scope.

## Architecture & Data Flow

The project references run inward: `Tui → Infrastructure → Application → Domain`. The three class-library layers are currently empty boundaries, not implemented services. Keep domain rules in Domain, use cases in Application, external adapters in Infrastructure, and terminal orchestration in Tui.

`Program.Main` calls `TuiApplication.Run`, which validates arguments and terminal interactivity, creates and runs the Terminal.Gui application, disposes the session, and maps `TerminalSessionOutcome` to an exit code. Argument rejection precedes redirection rejection. Diagnostics go to stderr after cleanup is attempted; preserve the stable outcome codes and avoid output during the UI loop.

## Key Directories

- `src/konstitech.DotNetDeck.Tui/` — executable and current terminal lifecycle.
- `src/konstitech.DotNetDeck.{Domain,Application,Infrastructure}/` — planned layer boundaries.
- `tests/konstitech.DotNetDeck.Foundation.Tests/` — .NET lifecycle and repository contract tests.
- `infra/woodpecker/`, `tests/infra/` — Linux CI configuration/preflight and Go tests.
- `.github/workflows/`, `.woodpecker/` — independent CI pipelines; `docs/woodpecker-linux.md` is the operator runbook.

## Development Commands

Run from the repository root:

```sh
dotnet restore dotnet-deck.slnx
dotnet build dotnet-deck.slnx -c Release --no-restore
dotnet run --project src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj
go test ./tests/infra/...
dotnet run --project tests/konstitech.DotNetDeck.Foundation.Tests/konstitech.DotNetDeck.Foundation.Tests.csproj -c Release --no-restore -- --treenode-filter "/*/konstitech.DotNetDeck.Foundation.Tests/*/*"
dotnet publish src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj -c Release -r linux-x64 -o artifacts/linux-x64
```

The TUI requires interactive stdin and stdout, accepts no arguments, and exits via Esc or Quit. Publish on a host compatible with the target RID. There is no separate configured lint command; the Release build treats warnings as errors.

## Code Conventions & Common Patterns

- Use file-scoped namespaces matching `konstitech.DotNetDeck.<Layer>`, explicit access modifiers, nullable reference types, and the existing C# conventions.
- Model terminal results with `TerminalSessionOutcome`; preserve typed status, exit-code and diagnostic mappings. `TuiApplication.Run` owns disposal of its application and window.
- Use the existing `Func<IApplication>` and `TextWriter` injection seams for terminal tests. There is no DI container, async workflow, persistence, or state store yet; do not assume one exists.
- Keep NuGet versions in `Directory.Packages.props`, not individual project references. Maintain NativeAOT and trimming compatibility; TUI publish treats AOT/linker warnings as errors.
- Session activity instrumentation exists, but no telemetry exporter is configured. Avoid claiming it is collected by default.

## Important Files

- `src/konstitech.DotNetDeck.Tui/Program.cs`, `TuiApplication.cs`, `TerminalSessionOutcome.cs` — entry point, lifecycle and outcome contract.
- `dotnet-deck.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props` — solution, SDK, shared compiler settings and package versions.
- `.github/workflows/native-aot.yml`, `.woodpecker/linux.yaml` — CI verification; GitHub Actions does not run the Go infrastructure suite.
- `infra/woodpecker/main.go`, `docs/woodpecker-linux.md` — infrastructure preflight and operational instructions.

## Runtime/Tooling Preferences

Use .NET SDK 10.0.115 or a stable 10.0.1xx patch accepted by `global.json`, C# 14 and Go 1.22 or later for infrastructure tooling. Git is needed for repository contract tests. NativeAOT needs host-native tools: `clang` and `zlib1g-dev` on Ubuntu, Xcode Command Line Tools on macOS, or the Visual Studio C++ desktop workload on Windows. The documented RIDs are `linux-x64`, `osx-arm64`, and `win-x64`.

Woodpecker setup and live service changes are operator activities, not ordinary development. Never commit secrets or expose webhook query tokens; Docker socket access is root-equivalent. Consult the runbook before infrastructure operations.

## Testing & QA

The .NET tests use TUnit and the executable Microsoft.Testing.Platform harness. Use `--treenode-filter`, not VSTest `--filter`; for a narrower run replace the last pattern above with `"/*/konstitech.DotNetDeck.Foundation.Tests/TuiApplicationTests/*"`. The assembly serializes tests because terminal and activity state are shared. Contract tests use disposable Git repositories and may require coordinated updates when project, documentation, or workflow contracts change.

Go infrastructure tests use the standard `testing` package and isolated fixtures; some secret ownership tests require root or UID 1000 and otherwise skip. No coverage threshold is configured. Automated tests and native publish do not establish terminal rendering or restoration: perform the separate real-terminal checks in `README.md` when changing the UI lifecycle.

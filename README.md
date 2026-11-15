# .NETDeck

## Status

This repository initializes the four planned .NET layer projects and a real Terminal.Gui lifecycle with a quit surface. The Domain, Application and Infrastructure class-library boundaries are intentionally empty; they are not working product capabilities. The four functional tabs and Zed integration are not implemented. Author: konstitech.

## Prerequisites

Use .NET SDK 10.0.115 or a stable 10.0.1xx patch accepted by `global.json`, with C# 14. The UI uses Terminal.Gui 2.5.0 and requires interactive standard input and output. Git is required for repository contract tests, not for running the product binary.

NativeAOT additionally needs platform-native build tools: on Ubuntu install `clang` and `zlib1g-dev`; on macOS select Xcode Command Line Tools (`xcode-select -p`); on Windows install Visual Studio's Desktop development with C++ workload. Publish on a host compatible with the target RID.

## Build and run

```sh
dotnet restore dotnet-deck.slnx
dotnet run --project src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj
```

Press Esc or activate Quit with mouse or keyboard to end the session. Naming the binary `dotnet-deck` does not install a `dotnet deck` global tool.

The internal session returns a value-based outcome mapped to process exit code 0 on normal exit, 2 for any arguments or redirected input/output, and 1 on a runtime failure. Diagnostics are written to standard error after cleanup is attempted; no application output is written during the terminal run loop. If native cleanup itself fails, restoration cannot be guaranteed; abrupt termination cannot be compensated. A failure writing the diagnostic may propagate. No state or log directory is created.

## NativeAOT

Publish on the matching host:

```sh
dotnet publish src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj -c Release -r linux-x64 -o artifacts/linux-x64
dotnet publish src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj -c Release -r osx-arm64 -o artifacts/osx-arm64
dotnet publish src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj -c Release -r win-x64 -o artifacts/win-x64
```

The expected executables are `artifacts/linux-x64/dotnet-deck`, `artifacts/osx-arm64/dotnet-deck`, and `artifacts/win-x64/dotnet-deck.exe`; binaries are not bundled in the repository.

Terminal.Gui declares AOT compatibility upstream, but its own project and NativeAOT smoke project suppress some trimming/AOT warnings. This project does not copy those suppressions: strict native publish is a compatibility gate, not proof of compatibility. Linux linux-x64 NativeAOT publish was observed before this portability remediation; macOS osx-arm64 and Windows win-x64 publish remain unverified. The amended tree and cross-platform CI remain unverified.

## Verification

```sh
dotnet restore dotnet-deck.slnx
dotnet run --project tests/konstitech.DotNetDeck.Foundation.Tests/konstitech.DotNetDeck.Foundation.Tests.csproj -c Release --no-restore -- --treenode-filter "/*/konstitech.DotNetDeck.Foundation.Tests/*/*"
dotnet publish src/konstitech.DotNetDeck.Tui/konstitech.DotNetDeck.Tui.csproj -c Release -r linux-x64 -o artifacts/linux-x64
file artifacts/linux-x64/dotnet-deck
```

The test framework is TUnit 1.85.0. The test project is an executable Microsoft.Testing.Platform harness; `--treenode-filter` selects tests rather than the VSTest `--filter`. Repository tests exercise a disposable local Git commit and fresh autocrlf checkout, plus controlled infrastructure failures; they do not modify the current repository's index or configuration, or prove Windows/macOS NativeAOT or real-terminal behavior.

The pre-remediation foundation suite passed 28 tests; the portability remediation has not yet been verified. These results and the Linux publish observation predate both the portability remediation and the TUnit migration; they establish no result for the migrated suite. The new verification gates remain unverified.

Real-terminal verification is a separate, currently unverified gate. On Linux record `stty -g`, run `./artifacts/linux-x64/dotnet-deck`, observe the UI and press Esc, then compare `stty -g` and check cursor and echo. Repeat and click Quit. On macOS use `./artifacts/osx-arm64/dotnet-deck` and the same checks. Check Windows independently on a Windows terminal. A redirected process or unit test does not establish interactive terminal behavior.

## Plan review

The [original project plan](Projektplan.NETDeck.v1.0-Lean-MVP.md) specifies .NET 10 and C# 14, not a .NET 11 override. Its filename was made portable without changing its authorized contents. Its under-15-ms shortcut-to-interactive goal is unmeasured, not an initialization result. Zed release ownership, download hooks, key bindings, focus return and gutter integration require later API feasibility design; no extension scaffold is shipped. A future CLI parser must verify the plan's combined outdated/vulnerable syntax. Future persistence and log paths must be cross-platform rather than copying Linux paths to Windows or macOS.

## License

[Apache License 2.0](LICENSE).

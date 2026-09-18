# ArcScope

Native C# desktop entry point for the ArcForges instrument acquisition and analysis product. This bootstrap provides a working Hello screen connected to the deployed Cloud Native AOT service at `https://arcforges.com/api`.

The application starts offline. Enter a name and select **Say hello** to send one binary gRPC-Web request through the published Contracts client. Progress, cancellation and failures are visible; retry is an explicit user action. Names retain whitespace and Unicode, with a 1–256 UTF-16 unit limit. Authentication and product-specific workflows are not implemented in this bootstrap.

## Run and develop

Install .NET SDK **10.0.401** and the native build prerequisites in [development](docs/development.md). The solution uses C# 14, Avalonia **12.1.2**, exact central NuGet versions and per-project lock files. No Node, Python, CMake or neighboring source checkout is required.

```sh
dotnet restore ArcScope.slnx --locked-mode
dotnet build ArcScope.slnx -c Release --no-restore
dotnet test --project tests/ArcForges.ArcScope.Tests/ArcForges.ArcScope.Tests.csproj -c Release --no-build
dotnet run --project src/ArcForges.ArcScope
dotnet run --project eng/ArcForges.Repository -- hooks
```

The UI, application state and repository tool are C#. Avalonia/Skia supply packaged native UI/rendering dependencies. The application consumes `ArcForges.Contracts.PublicApi` **1.0.0-ci.36.1** and the private build-time `ArcForges.Build.Policy` **1.0.0-ci.7.1**. It does not ship unused DesktopPlatform media engines.

## Downloads and automation

Each successful main push publishes a prerelease `v0.1.0-ci.<run>.<attempt>` to [GitHub Releases](https://github.com/ArcForges/ArcScope/releases). PRs build and test the same five Native AOT targets but never publish a release:

| Platform | Archive |
| --- | --- |
| Windows x64 / ARM64 | Portable ZIP; extract everything and run `ArcScope.exe` |
| Linux x64 | Portable tar.gz; extract everything and run `./ArcScope` in an X11/XWayland desktop |
| macOS Intel / Apple Silicon | tar.gz containing `ArcScope.app` and notices |

Keep all files together. These self-contained builds do not require a .NET installation. They are development distributions: Windows binaries are unsigned and macOS bundles are ad-hoc signed, not Developer ID signed or notarized. No installer, app-store identity or OS trust claim is included. Linux system libraries are listed in [development](docs/development.md). Source for a release is its exact Git tag/commit.

CI verifies the native window, UI action, live greeting, Unicode/boundaries, gRPC status and backend revision on each native host before packaging. `--smoke-live --evidence <absolute-path.json>` is an explicit network-using validation mode that closes the window afterward; it is not normal startup. See [release mechanics](docs/releasing.md) and [bootstrap plan/evidence](docs/bootstrap-plan.md).

## Contribute and report issues

The repository check enforces the [project licence boundary](docs/licence-boundary.md)
for the application, core library, tests and C# tooling. Every build also checks
the effective MSBuild declarations and local project references.

Read [CONTRIBUTING](CONTRIBUTING.md), [security reporting](SECURITY.md) and the [code of conduct](CODE_OF_CONDUCT.md). ArcScope remains **AGPL-3.0-only**; see [LICENSE](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).

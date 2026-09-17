# Development and verification

## Prerequisites

- .NET SDK 10.0.401; all build and test tools are C#/.NET.
- Windows: Visual Studio C++ desktop build tools with the native host architecture toolchain and Windows SDK for Native AOT.
- Linux: an X11/XWayland desktop, `libx11-6 libice6 libsm6 libfontconfig1`; native compilation uses `clang zlib1g-dev`. CI also installs `xvfb fonts-dejavu-core` and uses a virtual display.
- macOS: Xcode command-line tools. Native hosts build and run their matching RID. The tool creates an `.app` and applies ad-hoc signing before the live test.

No service token, `.env` file or Cloudflare account is needed to consume public Hello. Production authentication will be a separately designed feature. Startup performs no network request.

## Structure

- `src/ArcForges.ArcScope.Core`: owned published gRPC client and observable request state.
- `src/ArcForges.ArcScope`: native Avalonia window with typed C# event wiring and explicit Generic Host lifetime. No reflection binding, dynamic XAML or WebView.
- `tests/ArcForges.ArcScope.Tests`: deterministic loopback gRPC-Web, deadline/cancellation, UI state and release-integrity tests.
- `eng/ArcForges.Repository`: repository checks, local hooks, CI version, stage/smoke/pack and release verification.

`GrpcChannel` ignores base URI paths. A delegating handler prepends `/api` exactly once to the generated service path. Binary gRPC-Web uses HTTP/1.1, normal TLS, a five-second RPC deadline and bounded message sizes. HTTP redirects and cookies are disabled. There is no automatic application retry. The backend may return `Unavailable` during service trouble or `ResourceExhausted` when a limit is reached; the user chooses when to retry.

## Native local test (Windows example)

After the source checks in `CONTRIBUTING.md`, use a fresh output directory and the actual 40-character commit SHA in place of `COMMIT`:

```sh
dotnet publish src/ArcForges.ArcScope/ArcForges.ArcScope.csproj -c Release -r win-x64 --no-restore -p:Version=0.1.0-ci.0.1 -p:SourceRevisionId=COMMIT -o artifacts/publish/win-x64
dotnet run --project eng/ArcForges.Repository -c Release --no-build -- prepare win-x64 0.1.0-ci.0.1
dotnet run --project eng/ArcForges.Repository -c Release --no-build -- smoke win-x64
dotnet run --project eng/ArcForges.Repository -c Release --no-build -- pack win-x64 0.1.0-ci.0.1 COMMIT
```

Use the matching RID on another native host; run the smoke command under `xvfb-run -a` on a headless Linux host. Staging/archives are not overwritten. Remove only the task's own `artifacts` directory before starting another local candidate.

The explicit smoke starts the real Native AOT window, waits for Cloud health, fills the name field, invokes the window's button action, asserts the displayed live greeting and checks Unicode, maximum size and two application errors. It records a rendered window image, native AOT/RID/source/version and matching Cloud Worker/container revision. This is programmatic native UI validation, not a claim of manual interaction, accessibility, installer or trusted signing certification. Readiness polling does not retry a failed Hello RPC.

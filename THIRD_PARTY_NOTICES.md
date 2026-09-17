# Third-party components

ArcScope source is AGPL-3.0-only. Third-party components retain their own copyright and licences. `Directory.Packages.props` and committed `packages.lock.json` files identify the exact resolved dependency graph; the SDK is pinned in `global.json`.

| Component family | Upstream / licence |
| --- | --- |
| Avalonia (including native desktop backends) | [Avalonia](https://github.com/AvaloniaUI/Avalonia), MIT |
| SkiaSharp / HarfBuzzSharp managed integration | [SkiaSharp](https://github.com/mono/SkiaSharp), MIT; upstream native libraries include their own notices |
| Google Protocol Buffers | [protobuf](https://github.com/protocolbuffers/protobuf), BSD-3-Clause |
| gRPC for .NET | [grpc-dotnet](https://github.com/grpc/grpc-dotnet), Apache-2.0 |
| .NET runtime and Microsoft.Extensions | [dotnet](https://github.com/dotnet/runtime), MIT and included third-party notices |
| MicroCom.Runtime | [MicroCom](https://github.com/kekekeks/MicroCom), MIT |
| Tmds.DBus.Protocol | [Tmds.DBus](https://github.com/tmds/Tmds.DBus), MIT |
| ArcForges.Contracts.PublicApi | [Contracts](https://github.com/ArcForges/Contracts), Apache-2.0 |
| ArcForges.Build.Policy (build only) | [DesktopPlatform](https://github.com/ArcForges/DesktopPlatform), AGPL-3.0-only |
| xUnit and Microsoft Testing Platform (tests only) | Their NuGet package metadata and upstream licence files; not application dependencies |

Portable distributions include the project's licence, this notice, a machine-readable resolved-package inventory and licence/notice files available in the restored packages. For Avalonia, MicroCom, Tmds.DBus, gRPC, Protobuf and .NET packages that omit the primary licence text, `third-party/` includes the upstream text from the source commit declared in each package's metadata. `third-party/sources.json` records source URLs and SHA-256 hashes; these files are also included in every archive. OS libraries and fonts are provided by the host system, not copied into the application.

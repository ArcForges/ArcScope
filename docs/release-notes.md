Native AOT ArcScope Hello desktop client connected to `https://arcforges.com/api`.

Download the complete portable archive matching your OS and CPU, verify its SHA-256, and extract all files together. No .NET runtime installation or service token is needed. Windows: run `ArcScope.exe`. Linux: run `./ArcScope` in an X11/XWayland desktop with the documented system libraries. macOS: the archive contains `ArcScope.app`.

These are development prereleases. Windows binaries are unsigned; macOS bundles have ad-hoc signing only, with no Developer ID/notarization. Installer, store and automatic-update delivery are not included.

All five native candidates passed deterministic tests and programmatic native UI/live Cloud checks before this release was published. `arcscope-verification.tar.gz` contains source/RID/version manifests, live check results and rendered window images. The source is the commit referenced by this release tag. See the README at that tag for prerequisites and licence notices.

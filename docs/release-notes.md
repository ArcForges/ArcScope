# ArcScope development prerelease

Extract the complete Windows x64/ARM64 ZIP or Linux x64 tar.gz and keep its files together. No .NET runtime installation or service token is needed. Windows: run `ArcScope.exe`. Linux: run `./ArcScope` in an X11/XWayland desktop with the documented system libraries.

These are portable development builds. Windows binaries are unsigned; installer, store and automatic-update delivery are not included. This release has no macOS artifact.

The candidates passed Windows/Linux compilation and the applicable offline/static/security checks. CI does not launch applications or perform UI/live Cloud tests. `arcscope-verification.tar.gz` contains build source/RID/version manifests, not runtime proof or screenshots. The source is the commit referenced by this release tag. Checksum sidecars support optional integrity diagnostics and are not trusted code signatures. See the README at that tag for prerequisites and licence notices.

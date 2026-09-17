# ArcScope contributor and agent instructions

- Read `docs/bootstrap-plan.md`, `README.md` and the relevant source before changes. Collect concrete failures, decide a bounded fix, then implement and verify it.
- Keep this repository a C# desktop consumer. Use published, exact NuGet dependencies and committed locks. Do not add sibling source references or submodules.
- Keep AGPL-3.0-only headers and the existing licence. Do not import reference code under incompatible terms.
- Preserve Native AOT, normal TLS validation, `/api` routing, RPC deadlines, cancellation and user-visible failures. No fake success, credential in a desktop binary, blanket trimming suppression or automatic application retries.
- Keep the production entry point offline until a user action. Live smoke testing is explicit and must exercise the native UI action and published client.
- Run the checks in `CONTRIBUTING.md`. Never describe a local test, cross-compile or mock as a hosted native or live service test.
- PRs validate; only successful main pushes publish all five verified candidates. Do not merge a PR unless the user requests it.

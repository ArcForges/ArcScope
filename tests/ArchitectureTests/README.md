# ArcScope architecture policy tests

This package-free host runs the shared architecture-policy engine of the exact pinned `ArcForges.Build.Policy`
package over ArcScope's own repository (task GOV.07). It adds no test-framework dependency; the engine's `Xunit.FactAttribute`
identity is declared in `Program.cs`.

| Run | Command | What it proves |
|---|---|---|
| Local | `dotnet run --project tests/ArchitectureTests -c Release` | Offline positive and negative fixtures for every enforced rule, contract-consumption, licence-allowlist and workflow-order checks. It never claims the hosted evidence. |
| Hosted | `dotnet tests/ArchitectureTests/bin/Release/net10.0/ArcForges.ArcScope.ArchitectureTests.dll --hosted` | Only in the pull-request `quality` job, after the existing Gitleaks scan, a locked full build and the canonical forbidden-term scan: the engine over the real, SDK-compiled project graph, with RP-01, RP-08 and RP-09 bound to the exact commit, run and attempt. |

Rule coverage

- WP-05.00 layering (AT-01, AT-02, AT-04, AT-05, AT-09, AT-14, RP-07) and WP-05.01 licence boundary (RP-02, RP-03, dependency
  allowlist from `eng/policy/reuse-policy.json`): `ArchitectureFixtureTests`.
- WP-05.02 forbidden terms: `naming_evidence.py` loads the scanner and data from the exact published
  `ArcForges.Contracts.Validation` package pinned in `naming-package.json` (identity checked once at acquisition, two named
  assets extracted to a temporary directory), scans this repository, runs one positive and one negative fixture per forbidden
  term, and writes `artifacts/evidence/naming.json`, which the hosted gate re-reads.
- WP-05.03 generated-client consumption: `ContractConsumptionChecks` (exact central contract pins, no sibling source or binary
  reference, no authored schema or client generation, no hand-written wire type, descriptor or marshaller, binary gRPC-Web only,
  registered compile-time JSON metadata).
- WP-05.04 banned APIs: one allowed and one banned compiled fixture for each of the seven categories and each ArcScope role,
  plus the engine's real scan of production code. Findings that are carried are exact, owned and expiring rows in
  `eng/policy/exceptions.json`.

Project roles are reviewed in `HostedPolicyGate.Classifications`; the set must equal the solution. ArcScope is AGPL-3.0-only, so
no RP-03 exception exists or is permitted.

Known deferral (honest limit)

The shared engine fails closed ("Unresolved invocation cannot be audited") on the unmanaged function-pointer calls in
`src/ArcForges.ArcScope/NativePackageProof.cs`, so the executable is classified `Production: false` and its production-only rules
(banned-API scan, public-API test binding, layer rules that apply to production projects) are not enforced. Layering, licence, AOT
fence and contract-consumption rules still cover it. The deferral is self-expiring: a fixture and a hosted check assert that the
engine still cannot audit that file and fail once it can, which forces `Production: true` to be restored. Repairing the engine
is a shared Build.Policy change, not part of this task.

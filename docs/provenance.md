# Provenance and native distribution records

WP00.03 follows the [current Design profile](https://github.com/ArcForges/ArcForges-Design/blob/5322d698a1b650a52a5a139d986dd85b00b48581/docs/assurance/reference-coverage-and-provenance.md).
ArcScope's current repository is the implementation owner. Its dated bootstrap
history is retained; the retired initialization repository is not a required input.
The application continues to use native Avalonia/Skia and exact published packages.

Complete `eng/provenance/template.json` before introducing source, tests, assets,
generated material or legal text from another origin. Store a reviewed record as
`eng/provenance/records/<id>.json`, with exact repository/commit/paths, file-level
licence, attribution, target, disposition, verification oracle, NOTICE and lifetime.
Generated material identifies each generator and input separately. A temporary
record names its removal trigger and accountable owner. The Licensing and Provenance
Owner reviews the actual evidence; maintainer-authorized review may exercise that
role. An approval field alone is not evidence. Unresolved records under
`eng/provenance/conflicts` block acceptance.

The five-row decision table is closed policy. `eng/provenance/files.json` classifies
every tracked file and non-ignored new file; reused targets bind exact SHA-256 values.
Review also identifies copied content inside existing authored files. A script cannot
establish authorship. Initial legal records reconcile existing bytes without claiming
approval existed before their historical introduction. Legal-document copying terms
do not grant permission to import the implementation described by that document.

Used records are immutable and remain present after retirement. Create a revision
with `supersedes` for changed intent, source or content and update its active binding.
CI compares with the trusted event's base and requires history; retained external
files cannot silently become first-party files. The template is explanatory only.

The existing C# `check` command enforces these rules and the deterministic NOTICE.
After reviewing record/inventory changes, update the summary using
`dotnet run --project eng/ArcForges.Repository -- provenance-notice`, then run all
checks in `CONTRIBUTING.md`. Reports are retained under `artifacts/evidence`.

The original WP00.03 tooling record bound four files from reviewed ArcNotes
`e40423a1b14ce8341de35748cc2a093c7c9b77a7`, with only explicit product identity
substitutions. AGPL terms and the original Apache checker attribution/full licence
are preserved. This creates no sibling-source build dependency or new tooling runtime.
Every build uses this repository's committed files, locks and tests.

Native staging requires clean audited source. Portable archives retain the original
root licence, six complete upstream legal texts, a package source summary and the
actual source receipt. Packing and independent release verification inspect ZIP/tar
contents, including case-colliding/duplicate paths, traversal, links, changed/missing
notices and wrong/dirty source identities. Existing dependency notices remain intact.

All five native RID, Native AOT, real UI/live Cloud and publication gates remain
required. Deterministic checks, local Windows execution, hosted native results and
public release verification are separate evidence. They do not claim complete product
features, trusted OS signing or later commercial acceptance.

WP02.04 supersedes the tooling record with `arcnotes-provenance-tools-r2`: the reviewed ArcNotes merge `0c797e30690a10d8798ddca19ca7f37b16cecf01` supplies compiled identity and independent source resolution. The immutable r1 remains. The record binds every adapted file and the product-specific future format/storage declarations. Runtime support code is covered by the full AGPL licence and origin/source pointer included in portable README; checker-only Apache terms stay with corresponding source.

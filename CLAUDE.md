# AutoFill Studio — context for Claude

Generic platform: **source documents + template → AI fills → human reviews → export**. Customers create
"recipes" (template + slots + how each slot gets its value) themselves in the UI. Generalizes two earlier
projects by the same user: the "Soạn văn bản đi" app for Sở Ngoại vụ TP.HCM (Node, `C:\Users\ANC\Desktop\Tổng hợp mẫu VB\app`,
Word templates, live on Vercel) and SFDR auto-translate (`GTS-GR/automated-translate`, .NET, Excel rows → Dutch/French).
A third case to validate against: workflow-agent / B210 Crowe Excel (`C:\Users\ANC\Desktop\workflow-agent`).

Plan and status: `plans/261015-1505-generic-autofill-platform/plan.md` (7 phases). **All 7 phases coded by 2026-10-16. Left for the user: acceptance with real files + AI key (`docs/acceptance-checklist.md`) and deploying to the office machine (`docs/deployment.md`).**

## Decisions already made by the user (do not reopen)
- Separate project; never add these generic features to the Sở Ngoại vụ app (it has a live customer).
- Customers create recipes through the UI, no code.
- Backend .NET 10. UI = plain HTML/CSS/JS modules in `src/AutoFill.Web/wwwroot`, same look as the Sở Ngoại vụ app (no Blazor/React).
- Templates need **no red marking**: AI analyzes the template to find slots; the user confirms/edits.
- DB = PostgreSQL on a **new** Supabase project; files in a private Supabase Storage bucket.
- Deploy = Docker on an always-on **Windows** office machine, published via Cloudflare Tunnel at **https://ntdat.qzz.io/auto-fill/** (path, not subdomain: `BASE_PATH=/auto-fill`). UI URLs must stay relative (no leading `/`).
- Numbers/codes/Y-N come from rules or lookups, not AI. Every value carries evidence (page + quote).

## Conventions
- Build/test: `dotnet build`, `dotnet test` (embedded PostgreSQL, no Docker needed), `npm test` (UI helpers, `node --test`).
- New migration: `dotnet ef migrations add <Name> --project src/AutoFill.Data --startup-project src/AutoFill.Web --output-dir Migrations`.
- RLS is enabled automatically for every public table after migrations (`Database.MigrateAsync`); keep it that way.
- Workspace-owned entities implement `IWorkspaceOwned` and get a query filter in `AppDbContext`; background code sets `WorkspaceScope`.
- Long work runs as a job (`JobQueue` + an `IJobHandler`), UI follows it via `/api/jobs/{id}/events` (SSE).
- Source files are read through `SourceService` (cached in `transcripts` by sha256); bump `SourceReader.Version` when a reader's output changes.
- Templates: Word addresses come only from `WordParagraphs.Collect` (shared by outline and writer); never compute paragraph numbers or offsets another way. The AI copies words; placers compute offsets.
- Tests: DB test classes take `IClassFixture<PostgresServer>` + `[Collection(PostgresCollection.Name)]` (one embedded server per class, copied from a golden instance in `bin/.../pg_embed`; a shared server stalls on Windows after ~16 databases). Delete the golden folder if it ever gets corrupted.
- Runs: recipe versions are immutable (edit = new version). Engine order and strategy configs are documented in `plans/.../phase-04-run-engine.md`.
- UI pages: `index.html` (recipes pane + run pane), `recipe.html` (builder), `run.html` (review), `admin.html`. Pure helpers go in `slots.js` (tested by `node --test`); builder/run/review strings in `strings.js`. Enum values from the API are camelCase (`longText`).
- User-facing errors: throw `UserFacingException` with a Vietnamese message.
- Code comments in English; UI text in Vietnamese.

## Working with the user
- Reply in Vietnamese, the user is "anh", Claude is "em". Short, plain explanations; show options + recommendation before asking.
- Never read or print `.env` values; the user fills secrets in themselves; never ask for keys/passwords in chat.
- Data may contain personal information (passports, names) — keep it out of logs, commits and chat.
- Commit/push only when asked. Destructive cloud actions (deleting projects/buckets) are left to the user.
- On this machine: PowerShell is reliable; Bash sometimes hangs. Non-ASCII paths exist (`Tổng hợp mẫu VB`).

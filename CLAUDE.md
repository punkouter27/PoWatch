# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

`AGENTS.md` above holds the rules. [README.md](README.md) is the project overview (what the app is,
how to run it, pages, diagnostics); this file maps the pipeline. Read both before changing behavior.

## Commands

```powershell
docker compose up -d                                   # Azurite; storage is required, the app will not start without it
dotnet build PoWatch.slnx                              # warnings are errors
dotnet run --project src/PoWatch.Api --launch-profile http   # http://localhost (port 80); steps to the next free port if taken
dotnet run --project src/PoWatch.Api --no-launch-profile --urls http://localhost:5560   # an explicit --urls / ASPNETCORE_URLS wins over port 80
dotnet format PoWatch.slnx --verify-no-changes         # run before pushing (scripts/ci-checks.ps1 runs it; CI does not)
./scripts/check-hygiene.ps1                            # layout, naming, package versions, vocabulary, dead CSS + components
./scripts/check-test-caps.ps1                          # after a build: 100 unit / 50 integration / 25 API E2E / 25 UI E2E
./scripts/caption-eval.ps1 -Count 10                   # score the caption models (Node; first run downloads ~1 GB)
```

Tests, narrowest first (integration and E2E start Azurite through Testcontainers, so Docker must be up):

```powershell
dotnet test tests/PoWatch.Unit --filter "FullyQualifiedName~RollupTests"
dotnet test tests/PoWatch.Integration
dotnet test tests/PoWatch.E2EAPI
dotnet test tests/PoWatch.E2EUI -c Release              # needs Playwright's Chromium and Docker (local only; CI runs only format + Unit)
```

E2EUI hosts the app in-process against an Azurite Testcontainer by default (since 2026-11-17; it used to
pass every test silently unless `E2E_LOCAL=1`). `E2E_BASE_URL` points it at a running instance instead. It
never skips: no Docker or no Chromium fails every test with the reason.

## Where the pipeline lives

Sensing runs in the browser; the server only ingests 10-second ticks and serves rollups.

- **Browser sensing:** `PoWatch.Client/wwwroot/js/sensing/` (pixel layer, RF-DETR detector worker) and
  `js/sensing/inference-worker.js` (captions: models in `js/caption-models.js`, keep/reject rules in
  `js/caption-quality.js`, MiniLM embeddings in `js/embedder.js`), driven from `Client/Services/SensingSession.cs`. The
  browser-safe C# parts (`CentroidTracker`, `TickBatcher`, `VlmScheduler`, `AdaptiveCadence`,
  `HighlightRules`, `PostureRules`, `ReelPlanner`) sit in `PoWatch.Shared/Services` so unit tests can cover them. Unsent batches wait
  in the IndexedDB outbox (`js/outbox.js`).
- **Ingest:** `Api/Features/Ingest` → `Application/Features/Ingest/IngestService.cs`. It claims each `batchKey`
  once in `PoWatchIngestLedger`, writes raw rows and merges minute/hour/day/all rollups; the endpoint
  then pushes `statsChanged` through `NotifyStatsChangedAsync` (`Api/Features/Stats/StatsHub.cs`).
- **Reads:** `StatsQueryService` reads rollups and calls the pure stat math in `PoWatch.Domain/Services`
  (`PresenceStats`, `PatternStats`, `AnomalyMath`, …). New stats usually mean Domain math plus a rollup field.
- **Storage:** interfaces in `Application/Contracts`, Azure Table/Blob implementations in
  `Infrastructure/Persistence`. Every table is partitioned by user, except `PoWatchShares` (by token, so a
  public link is one point read); anything that deletes a user's data must also delete their shares by owner.
- **API:** one folder per feature under `Api/Features`, each exposing a `Map*Feature()` extension that
  `Program.cs` calls. Integration tests run the real host against Azurite via
  `tests/PoWatch.Integration/Infrastructure/AzuriteWebApplicationFactory.cs`.
- **Background jobs:** `Api/Hosting/BackgroundJobs.cs` runs retention (`RetentionService`) and the weekly
  email (`DigestMailer`) on timers; both are off in the `Test` environment, so tests call the services directly.
- **Recaps:** `TemplateRecap` always writes the numbers; an optional `IChatClient` (`AiProvider:Provider`
  = `AzureOpenAi`, `Gemini` or `Ollama`, built in `Infrastructure/Runtime/RecapAi.cs`) may only reword them, and
  `RecapPrompt.KeepsToFacts` rejects any invented number, digits or words. The same client serves Ask
  (`AskService`, four tools) and `AssistService` (watch-rule drafts, the digest headline).

## Things that are easy to get wrong

- **Po shell (Po web standard, 2026-11).** `Layout/TerminalHeader.razor` is the one header, `.po-header`,
  on every page signed in and out (`BareLayout` renders it `Bare`: sign-in and `/diag`): `PoWatch`
  upper-left, then the signed-in email (`.po-header__email`; the dev guest is `guest@guest.localhost`)
  or Sign in, and the gear (`.po-header__settings`, a link to `/settings`) upper-right. The bell's links
  still open Settings in the drawer. `css/po-tokens.css` is the Po standard's file (identical in every
  Po app, never edited here): Inter + Space Grotesk self-hosted in `wwwroot/fonts/`, one indigo accent,
  light/dark follow `prefers-color-scheme` (no theme switch, no `data-theme`; the canvases listen to the
  media query). `css/terminal-tokens.css` aliases the `--term-*` palette onto `--po-*`. The build stamp
  is one `<span class="po-version">v{YYMMDD}</span>` (the HEAD commit's UTC date, `PoBuildStamp` in
  `Directory.Build.props`, read as `BuildDate` metadata by the client and `/api/diag`), lifted above
  the phone bottom bar by `--po-stamp-lift`. Tab with nothing focused opens `/po-dev.html`
  (`js/po-devwindow.js`).
- **Diagnostics routes (2026-11-17).** `/diag` is the client page (anonymous, `BareLayout`) fed by the
  anonymous `GET /api/diag`, which rewrites every health check in its own words: never return a check's
  description or exception message there (exception type at most; `EndpointContractTests` checks). The
  boot probe is `GET /api/diag/boot`; nothing on the server may map `/diag` itself. `/system` is the
  signed-in view and embeds the same `DiagStatus` component: groups API / Data / Services, one
  `.po-diag__card` with a `.po-badge--ok|warn|bad` per dependency. (Live `/diag` answered the in-app
  "Page not found" until 2026-11-18 because production was still on 663daec: the commit that added the
  page, and the two after it, never deployed — the old workflow's E2EUI step failed on Hugging Face CDN
  downloads, `net::ERR_NETWORK_CHANGED`, in `ViewportFitE2ETests`.)
- **Forms (2026-11-17).** Every form that submits data uses `RadzenTemplateForm` + `FluentValidationValidator`
  (`Client/Layout`) with a validator from `PoWatch.Shared/Validation`; the endpoint runs the same validator
  through `ApiParsing.Invalid` and answers `Results.ValidationProblem`. Errors show inline
  (`ValidationMessage`), and the submit button is disabled while saving.
- **Phone navigation.** Under 720 px the header's key bar (`NavBar`, `nav-*` test ids) hides and
  `Layout/BottomNav.razor` (`bottom-nav-*`) carries Live, Journal and System.

- **Sign-in in Development.** Every page needs a session. Open `/auth/login/fake?returnUrl=/` to become
  the guest; `POST /api/dev/seed?days=60&tz=America/New_York` fills it with history (once per user).
- **Layers.** Domain → Application → Infrastructure/Api; Shared is DTOs and browser-safe logic only.
  `ArchitectureBoundaryTests` fails the build on a reference that points the wrong way.
- **The client is trim-analyzed.** Anything serialized in `PoWatch.Client` goes through `PoWatchJsonContext`
  (source-generated); add new DTOs there.
- **Folders.** At most two directory levels under each project; scripts live in `scripts/`.
- **PDFs fail on Windows on ARM.** QuestPDF ships no `win-arm64` native library, so on an ARM machine every
  PDF endpoint answers 503 (the client shows why in a toast). Production is x64 and unaffected; to make PDFs
  locally, install the x64 .NET 10 SDK and run with `-r win-x64`.
- **Pushing to `master` deploys to production.** `.github/workflows/deploy.yml` is the Po workflow: the same
  file in every Po .NET web repo, each repo owning its copy. It checks formatting, builds, runs the Unit tier,
  publishes, deploys and waits for `/health/live` to serve the pushed commit; a red check stops the deploy.
  No other tests, no infra job, no rollback, no PR runs. Run the narrow tests and `scripts/ci-checks.ps1` (hygiene, test caps) locally
  before pushing.

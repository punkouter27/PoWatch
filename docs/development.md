# PoWatch development guide

[AGENTS.md](../AGENTS.md) holds the standard and the rules. [README.md](../README.md) is the project overview
(what the app is, how to run it, pages, diagnostics); this file maps the pipeline. Read both before
changing behavior. Paths below are relative to the repo root.

## Commands

```powershell
docker compose up -d                                   # Azurite; storage is required, the app will not start without it
dotnet build PoWatch.slnx                              # warnings are errors
dotnet run --project src/PoWatch.Api --launch-profile http   # http://localhost (port 80); steps to the next free port if taken
dotnet run --project src/PoWatch.Api --no-launch-profile --urls http://localhost:5560   # an explicit --urls / ASPNETCORE_URLS wins over port 80
dotnet format PoWatch.slnx --verify-no-changes         # run before pushing (scripts/ci-checks.ps1 and CI run it)
./scripts/check-hygiene.ps1                            # layout, naming, package versions, vocabulary, dead CSS + components
./scripts/check-test-caps.ps1                          # after a build: caps 100 unit / 50 integration / 25 API E2E / 25 UI E2E
                                                       # real counts (2026-11-20): Unit 58, Integration 23, E2EAPI 14, E2EUI 25
./scripts/caption-eval.ps1 -Count 10                   # score the caption models (Node; first run downloads ~1 GB)
```

Tests, narrowest first (Integration, E2EAPI and E2EUI start Azurite through Testcontainers; without Docker
they skip with one clear message, and E2EUI also skips without Playwright's Chromium or, with
`E2E_BASE_URL` set, without an app answering there). CI runs Unit and Integration:

```powershell
dotnet test tests/PoWatch.Unit --filter "FullyQualifiedName~RollupTests"
dotnet test tests/PoWatch.Integration
dotnet test tests/PoWatch.E2EAPI
dotnet test tests/PoWatch.E2EUI -c Release              # needs Playwright's Chromium and Docker (local only; CI runs only format + Unit)
```

E2EUI hosts the app in-process against an Azurite Testcontainer by default (since 2026-11-17; it used to
pass every test silently unless `E2E_LOCAL=1`). `E2E_BASE_URL` points it at a running instance instead. It
never skips: no Docker or no Chromium fails every test with the reason.

## Planning a change: `/grill-with-docs`

New features start with an interview, not a guessed spec. Three project skills live in
[.github/skills/](../.github/skills) and load in Copilot (VS Code and CLI) and in Claude Code:

- **`grill-with-docs`** (type `/grill-with-docs`) — interviews you about a plan or design, in rounds, and records
  what settles as it goes. This is the one you invoke.
- **`grilling`** — the interview: the design tree, the frontier, one numbered round with a recommendation per
  question on a `➡️` line, then wait. Facts come from the code, decisions come from you.
- **`domain-modeling`** — the writing: a resolved term goes into `GLOSSARY.md` at the root the moment it settles,
  and a decision is offered as an ADR in `docs/adr/` only when it is hard to reverse, surprising without context
  **and** the result of a real trade-off. Most sessions earn none.

`grill-with-docs` is a one-line delegation to the other two, so all three are committed: the wrapper alone gives
you an interview that writes nothing. `GLOSSARY.md` and `docs/adr/` are created on the first term or ADR, never in
advance, and `GLOSSARY.md` holds vocabulary only — no implementation detail, no spec, no scratch notes. The session
ends at shared understanding and stops there; nothing is built until you say so.

## Where the pipeline lives

Sensing runs in the browser; the server only ingests 10-second ticks and serves rollups.

- **Browser sensing:** `PoWatch.Client/wwwroot/js/sensing/` (pixel layer, RF-DETR detector worker) and
  `js/sensing/inference-worker.js` (captions: models in `js/caption-models.js`, keep/reject rules in
  `js/caption-quality.js`, MiniLM embeddings in `js/embedder.js`), driven from `Client/Services/SensingSession.cs`.
  Faces go through `js/face-overlay.js` (a fixed canvas laid over the docked video; follows each face
  as a slot) and `js/sensing/face-worker.js` (MediaPipe Face Landmarker, 478 points, names in
  [GLOSSARY.md](../GLOSSARY.md)); its runtime is vendored at `wwwroot/lib/mediapipe-1.2.3/` and only the
  ~4 MB `.task` model is downloaded at run time. It is a module worker, so it must load the `_module_`
  wasm pair (`forVisionTasks(WASM, true)`); the classic pair fails every frame with "ModuleFactory not set."
  Every 1.5 s one face crop goes to `js/sensing/face-reader-worker.js` (CPU, ~190 MB on first use): age
  band, presentation and a DINOv2 descriptor. Both reach `Client/Services/SensingSession.Faces.cs` as
  `[JSExport]` typed arrays and live in `FaceReadings` for the session only; `MediaPipeFace` maps the
  mesh to the 68 iBUG points `FaceGeometry` scores. The
  browser-safe C# parts (`CentroidTracker`, `TickBatcher`, `VlmScheduler`, `AdaptiveCadence`,
  `HighlightRules`, `PostureRules`, `FaceGeometry`, `FaceLabeler`, `MediaPipeFace`, `FaceReadings`, `ReelPlanner`) sit in `PoWatch.Shared/Services` so unit tests can cover them. Unsent batches wait
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
  `RecapPrompt.KeepsToFacts` rejects any invented number, digits or words. The same client serves
  `AssistService` (watch-rule drafts, the digest headline). There is no free-form Ask (removed 2026-11-23).

## Things that are easy to get wrong

- **Page shell (AGENTS.md).** `Layout/PoHeader.razor` is the one header, `.po-header`,
  on every page signed in and out (`Layout/MainLayout.razor` is the one layout; signed out it renders the header `Bare`, with no section keys, bottom bar, session controls or camera): `PoWatch`
  upper-left, then the signed-in email (`.po-header__email`; the dev guest is `guest@guest.localhost`) and the gear
  (`.po-header__settings`, a link to `/settings`), or signed out only the Sign in button (`po-link-btn filled small`)
  upper-right. The sign-in page itself shows the title and one button, Microsoft, in every environment, with nothing in
  the header's right side (local and Test hosts sign in at `/auth/login/fake`, which has no button). The bell's links
  still open Settings in the drawer. `css/po-tokens.css` is this repo's copy of the Po conventions: Inter + Space Grotesk self-hosted in `wwwroot/fonts/`, one indigo accent,
  light/dark follow `prefers-color-scheme` (no theme switch, no `data-theme`; the canvases listen to the
  media query). `css/terminal-tokens.css` aliases the `--term-*` palette onto `--po-*`. The version badge
  is one `<footer class="po-version">v{YYMMDD}</footer>` (`Layout/BuildVersion.razor`; the HEAD commit's UTC date,
  `PoBuildStamp` in `Directory.Build.props`, read as `BuildVersion` metadata by the client and `/api/diag`), the last
  thing in `<main>`: fixed in the corner on wide screens, in the page flow after the content on phones. Tab with nothing focused opens `/po-dev.html`
  (`js/po-devwindow.js`).
- **Diagnostics: one page, one API.** `/diag` ("System status", `Features/Diagnostics/Diag.razor`, anonymous,
  the bare shell signed out, the full app shell signed in) follows the /diag contract in AGENTS.md: banner, then API / Data / Services from the anonymous
  `GET /api/diag` (`DiagReportDto`). Storage comes from the health checks; every external service (Microsoft
  sign-in, Key Vault, Azure OpenAI, Gemini, Ollama, Open-Meteo, the digest email, Application Insights) is a free
  live call in `Features/Diagnostics/ServiceProbes.cs`. Each check has a 10 s deadline, and the whole report (health
  checks + probes) is run once and cached 60 s in `Features/Diagnostics/HealthSnapshot.cs`, which feeds both `/health`
  and `/api/diag`: two loads inside a minute show the same `checkedAt`. A service switched off on purpose is `Off`
  (grey, no latency, never counted in the banner). The endpoint rewrites every check in its own words: never return a check's
  description or exception message there (exception type at most; `EndpointContractTests` checks). A
  signed-in operator also gets `DiagOperator` under the sections: server runtime (`GET /api/diag/runtime`),
  the on-device inference engine and the pipeline counters. Browser errors post to `/api/diag/client-error`.
  The boot probe is `GET /api/diag/boot`; nothing on the server may map `/diag` itself. There is no `/system`
  page or `/api/diagnostics` route any more.
- **Radzen controls (2026-11-21).** Every button, text box, dropdown, number box and checkbox is a Radzen
  component. The theme is imported into `@layer vendor` (`index.html`), so the app's sheets win; class `pw-plain`
  (`terminal-base.css`, `@layer plain`) reverts a control to the native look so `.term-button` and friends style
  it as before. String-keyed dropdowns render options through `Layout/PickOption` (`data-value`, no runtime
  binder in the trimmed build); tests pick them with `PickAsync` / `ExpectPickedAsync` (`PoWatchPage.cs`).
  Exceptions are listed in AGENTS.md.
- **Forms (2026-11-17).** Every form that submits data uses `RadzenTemplateForm` + `FluentValidationValidator`
  (`Client/Layout`) with a validator from `PoWatch.Shared/Validation`, registered by type (`AddScoped<IValidator<X>, XValidator>` in
  both `Program.cs` files) and injected, never `new`ed; the endpoint runs the same validator
  through `ApiParsing.Invalid` and answers `Results.ValidationProblem`. Errors show inline
  (`ValidationMessage`), and the submit button is disabled while saving.
- **Phone navigation.** Under 720 px the header's key bar (`NavBar`, `nav-*` test ids) hides and
  `Layout/BottomNav.razor` (`bottom-nav-*`) carries Live, Journal and System (`/diag`).

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
- **Pushing to `master` deploys to production.** `.github/workflows/deploy.yml` is the only workflow
  (shape in AGENTS.md): format check, build, Unit and Integration tiers, publish, deploy, then wait for
  `/health/live` to serve the pushed commit; a red check stops the deploy. A second job, `mobile`, builds
  the Android target of `src/PoWatch.Mobile` without blocking the deploy. No infra job, no rollback, no PR
  runs. Run the narrow tests and `scripts/ci-checks.ps1` (hygiene, test caps) locally before pushing.
  Markdown anywhere in the tree and `.github/skills/**` never deploy: the workflow's `paths-ignore` is
  `['**/*.md', '.github/skills/**', 'SCREENSHOTS/**']`, so a prose-only push starts no run at all.

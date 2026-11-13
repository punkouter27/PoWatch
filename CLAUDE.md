# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@AGENTS.md

`AGENTS.md` above holds the rules. `docs/README.md` is the project overview (pipeline, pages, API,
storage); read it before changing behavior.

## Commands

```powershell
docker compose up -d                                   # Azurite; storage is required, the app will not start without it
dotnet build PoWatch.slnx                              # warnings are errors
dotnet run --project src/PoWatch.Api --launch-profile http   # http://localhost (port 80); steps to the next free port if taken
dotnet format PoWatch.slnx --verify-no-changes         # the CI formatting gate
./scripts/check-hygiene.ps1                            # layout, naming, package versions, vocabulary, dead CSS + components
./scripts/check-test-caps.ps1                          # after a build: 100 unit / 50 integration / 25 API E2E / 25 UI E2E
./scripts/caption-eval.ps1 -Count 10                   # score the caption models (Node; first run downloads ~1 GB)
```

Tests, narrowest first (integration and E2E start Azurite through Testcontainers, so Docker must be up):

```powershell
dotnet test tests/PoWatch.Unit --filter "FullyQualifiedName~RollupTests"
dotnet test tests/PoWatch.Integration
dotnet test tests/PoWatch.E2EAPI
$env:E2E_LOCAL = '1'; dotnet test tests/PoWatch.E2EUI -c Release   # needs Playwright's Chromium (CI installs it)
```

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
- **Pushing to `master` deploys to production.** The deploy gate is `GET /health` returning 200.

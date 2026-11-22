# PoWatch

PoWatch is a fun, stat-heavy webcam observer. Point a camera at anything, press Start, walk away, and
come back to a nerdy statistical picture of what happened: presence, motion, space, objects, recurring
"regulars", time patterns, anomalies, environment, captions, and pipeline telemetry. The
app runs as a hosted Blazor WebAssembly client served by `PoWatch.Api`; the API owns authentication,
storage, telemetry, diagnostics, and vertical feature endpoints.

The browser runs the sensing locally (pixel metrics, an object detector, and a small vision-language
model) against the device camera. Frames never leave the device except chosen highlight snapshots.
Compact 10-second ticks are posted through the BFF boundary, stored in Azure Table Storage (Azurite
locally), and rolled up into minute/hour/day/all-time stats.

### Phone sensor (`src/PoWatch.Mobile`)

A native .NET MAUI app (Android/iOS; not in `PoWatch.slnx`, not built by CI) turns a phone — an old
one on a shelf is the point — into the same sensor, posting the same ticks:

- **Native camera + on-device ML.** Camera2 (Android) / AVCapture (iOS) frames go through a C# port
  of the web pixel layer (16×9 grid, threshold 28, Rec.709 luma, 4-bit palette) and the same detector,
  RF-DETR nano int8 (`onnx-community/rfdetr_nano-ONNX`, downloaded once, ~29 MB, resumable) on ONNX
  Runtime (`Microsoft.ML.OnnxRuntime`) with NNAPI / Core ML when the phone has them; same 0.5
  threshold, COCO labels and colour signature, then the shared `CentroidTracker`, `TickBatcher` and
  `HighlightRules`. Highlights upload a snapshot like the web; regulars are observed the same way.
  Posture (ViTPose) and captions (VLM) are not on the phone yet, so those tick fields stay empty.
- **Sensor mode.** Android runs observing as a camera + microphone foreground service with a
  partial wake lock (screen off; Stop is on the notification); iOS keeps the screen on, dimmed. A
  near-black dim screen, keep-screen-on switch, and the detector slows on low battery (3 s) or heat
  (5 s, paused when critical).
- **Offline tick buffer.** Every closed batch is written to the phone first (one file per batch,
  oldest first, capped at two days) and posted from there; the server's `batchKey` ledger makes
  resends safe. Offline stops are retried.
- **Phone sensors.** Ticks carry three optional fields, null/0 from browsers: `AmbientLux` (light
  sensor, Android), `PressureHpa` (barometer) and `DeviceMovedSeconds` (accelerometer: the camera was
  bumped). They are validated (`Tick.Validate`), stored on the tick row only when present, and the
  scene narrator mentions them (light-meter range, a pressure swing ≥ 2 hPa, the camera moving).

Pages: Live (preview, readings, outbox counters, sensor mode), Journal (week digest, Ask, days and
sessions → day recap with moments and snapshots, presence, objects, light; session story), Settings
(profile, regulars rename/merge, watch rules per phone, share links, AI model, CSV export, delete
data, server), System status. Sign-in: Microsoft (Entra PKCE → bearer, needs redirect
`powatch://auth`) or, in Debug, the dev guest. Dev loop: run the API with
`--urls http://localhost:5560`, then `pwsh scripts/mobile-dev.ps1 -ApiPort 5560 -Run`.

The project targets .NET 10 from `global.json` and uses central package management. The client and shared DTO assembly are trim-analyzer clean, using source-generated JSON metadata instead of reflection-heavy serialization. Authentication is BFF-style: Microsoft Entra ID or dev/test guest sign-in creates an encrypted HttpOnly cookie, while the WASM client only asks `/auth/me` for state.

## Local Setup

Run the setup script from the repository root:

```powershell
.\scripts\setup.ps1
```

The script prepares local tooling, starts Azurite through Docker Compose, and walks the Azure login path when needed. The development API listens on `http://localhost` (port 80, set by `PoWatch:Ports:Http` in
`appsettings.Development.json`) and `https://localhost:5001`. If either port is taken, the dev host
steps to the next free one and logs it; an explicit `--urls` (or `ASPNETCORE_URLS`) is used as given
instead. Sign in at `/auth/login/fake?returnUrl=/` (guest, Dev/Test only).

Useful commands:

```powershell
dotnet restore PoWatch.slnx
dotnet build PoWatch.slnx
dotnet test tests/PoWatch.Unit --no-build
dotnet run --project src/PoWatch.Api/PoWatch.Api.csproj
```

Test suites have maximums of 100 unit, 50 integration, 25 API E2E, and 25 UI E2E cases.
After building, run `./scripts/check-test-caps.ps1` and `./scripts/check-hygiene.ps1`.
For UI verification, install Chromium using the suite's generated `playwright.ps1` and run
`pwsh scripts/run-browser-tests.ps1 -Repeat 3`: it publishes the trimmed Release build (what deploys), starts Azurite
and the published app, and runs the browser tier against it three times in a row. `dotnet test
tests/PoWatch.E2EUI -c Release` is the quicker untrimmed run (the app hosted in-process, or `E2E_BASE_URL`);
without Docker or Chromium it skips with the reason.

Shell (the Po web standard): one header on every page, signed in or out, with `PoWatch` upper-left and
the signed-in email and the settings gear (signed out: Sign in) upper-right; a `v{YYMMDD}` version badge
bottom-right (the HEAD commit's UTC date; on phones it follows the page content); Radzen controls; Inter + Space Grotesk, one indigo accent, and light/dark
that follow the device (`css/po-tokens.css`). Pressing Tab with nothing
focused opens a developer window (`/po-dev.html`) with live server and page stats.

Diagnostics: `/diag` ("System status") is a plain-language status page (API, data, services, version,
environment) that works signed out, fed by the anonymous `GET /api/diag`, which checks every external service with a live call
(services switched off on purpose show "Off"); signed in, it also shows the
server runtime, inference engine and pipeline. `GET /api/diag/boot` is the boot-readiness
probe (it was `/diag/boot` until 2026-11-17). On a phone, the sections are a bottom tab bar (`BottomNav`).

Session and day recaps (and their PDFs) are written by a template by default. Set
`AiProvider:Provider` to `AzureOpenAi` to let a model rewrite the
paragraph; the numbers always come from the stats, and the template takes over on any timeout, error
or invented number.
Pushing to `master` deploys to production through `.github/workflows/deploy.yml` (the only workflow):
format check, build, Unit and Integration tests, publish, deploy, then wait for `/health/live` to serve
the pushed commit; a `mobile` job builds the Android client beside it. Run the E2E tiers and
`scripts/ci-checks.ps1` locally before pushing. Bicep reads
resource names from `infra/deployment.json`; CI reads the app and resource group from the repo
variables `AZURE_WEBAPP_NAME` and `AZURE_RESOURCE_GROUP`.

## Documentation

- `AGENTS.md` — the Po web app standard this repo follows, and the rules for any agent (or human) working in it.
- `docs/development.md` — where the pipeline lives (sensing, ingest, reads, storage, API) and what is easy to get wrong.
- `CLAUDE.md` — one line pointing at `AGENTS.md`.

This `README.md` stays at the level of "what is this app and how do I run it".


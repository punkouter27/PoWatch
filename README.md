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

The project targets .NET 10 from `global.json` and uses central package management. The client and shared DTO assembly are trim-analyzer clean, using source-generated JSON metadata instead of reflection-heavy serialization. Authentication is BFF-style: Microsoft Entra ID or dev/test guest sign-in creates an encrypted HttpOnly cookie, while the WASM client only asks `/auth/me` for state.

## Local Setup

Run the setup script from the repository root:

```powershell
.\scripts\setup.ps1
```

The script prepares local tooling, starts Azurite through Docker Compose, and walks the Azure login path when needed. The development API listens on `http://localhost` (port 80, set by `PoWatch:Ports:Http` in
`appsettings.Development.json`) and `https://localhost:5001`. If either port is taken, the dev host
steps to the next free one and logs it. Sign in at `/auth/login/fake?returnUrl=/` (guest, Dev/Test only).

Useful commands:

```powershell
dotnet restore PoWatch.slnx
dotnet build PoWatch.slnx
dotnet test tests/PoWatch.Unit --no-build
dotnet run --project src/PoWatch.Api/PoWatch.Api.csproj
```

Test suites have maximums of 100 unit, 50 integration, 25 API E2E, and 25 UI E2E cases.
After building, run `./scripts/check-test-caps.ps1` and `./scripts/check-hygiene.ps1`.
For isolated UI verification, install Chromium using the suite's generated `playwright.ps1`, set
`E2E_LOCAL=1`, and run `dotnet test tests/PoWatch.E2EUI -c Release`.

Session and day recaps (and their PDFs) are written by a template by default. Set
`AiProvider:Provider` to `AzureOpenAi` to let a model rewrite the
paragraph; the numbers always come from the stats, and the template takes over on any timeout, error
or invented number.
Pushing to `master` deploys to production. Production resource names are shared
between Bicep and CI in `infra/deployment.json`.

## Documentation

- `AGENTS.md` — rules for any agent (or human) working in this repo.
- `docs/README.md` — the project overview: pipeline, stat families, pages, API, storage.

This `README.md` stays at the level of "what is this app and how do I run it".


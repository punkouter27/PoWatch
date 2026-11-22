# AGENTS.md

## 1. Standard

> This repo owns every file in it. Nothing here is generated from, synced with, or required to stay identical to
> another repository. Headers like "SAME FILE IN EVERY PO …", "Generated from po-shared … DO NOT EDIT", and scripts
> that copy files into or check other repos are forbidden. Only Azure resources in the `PoShared` resource group
> may be shared.

### Po Web App Standard

This repo is one of several independent Po apps. It follows the conventions below, but it owns every file:
nothing is generated from, synced with, or required to stay identical to another repository. Change anything
here freely; only the *conventions* are common, never the code. Azure resources may be shared through the
`PoShared` resource group; code may not.

#### Repo skeleton
```
README.md          what the app is, live URL, how to run locally, how it deploys
AGENTS.md          this standard + repo-specific notes (CLAUDE.md just points here)
CLAUDE.md          one line: "Read AGENTS.md."  (plus repo-specific notes only if unavoidable)
<App>.slnx  global.json  Directory.Build.props  Directory.Packages.props  .editorconfig
src/<App>.Api  src/<App>.Client  src/<App>.Shared  [src/<App>.Mobile]
tests/<App>.Unit  tests/<App>.Integration  [tests/<App>.E2E...]
infra/main.bicep  infra/main.bicepparam  [infra/modules/*.bicep]
scripts/           setup / helper scripts (no infra/*.ps1)
docs/              lowercase; everything else that is prose
.github/workflows/deploy.yml   the ONLY workflow
```
No `.cursorrules`, `.copilotignore`, `SPEC.md`, `tasks/`, `DOCS/` at the root (fold useful content into docs/).
`SCREENSHOTS/` is allowed only if README references it.

#### Page shell (every page, signed in or out, including the sign-in page)
- Header `.po-header`, height 56px, sticky: app name top-left (`.po-header__title`, links to `/`).
- Top-right `.po-header__actions`: signed-in user's **email** (`.po-header__email`, full email, ellipsis at 16ch on
  phones) — or, signed out, one **Sign in** button `class="po-link-btn filled small"` (same style on every page) —
  then the settings gear ONLY if the app has a settings page (the owner removed settings from some apps — don't
  re-add it). Apps without OAuth show nothing in place of the email (no fake identity chips).
- Version badge `.po-version` fixed bottom-right showing `vYYMMDD`. It must never cover content: `main` gets
  `padding-bottom: calc(var(--po-space-6) + env(safe-area-inset-bottom))`, and when a mobile bottom nav exists the
  badge sits above it. On phones (≤ 768 px) the badge leaves the corner and follows the page content (here: it is
  the last child of `<main>`, `position: static`).
- Colours only from `wwwroot/css/po-tokens.css` (`--po-*`). All text ≥ 4.5:1 contrast in light AND dark mode
  (status banners: use `--po-text` on `--po-*-soft` backgrounds, never warning-on-peach).
- Login-only data must not be requested for signed-out visitors (no 401s in the console on public pages), and
  actions a signed-out visitor cannot perform are hidden.

#### Version
- `Directory.Build.props` stamps ONE assembly metadata key: `BuildVersion` = UTC `yyMMdd`. No `BuildStamp`/`BuildDate`
  aliases. Every reader (header badge component, `/api/diag`, `/health` headers) reads `BuildVersion`.

#### Health + /diag contract
- `GET /health/live` (liveness, returns header `X-Commit: <sha>`), `GET /health` (readiness), `GET /api/diag` →
  ```
  DiagReportDto(string Status, string Environment, string Version, DateTimeOffset CheckedAt, double CheckedMs,
                DiagCheckDto[] Checks)
  DiagCheckDto(string Name, string Group /* "api" | "data" | "service" */, string Label, string Status
               /* Healthy|Degraded|Unhealthy */, string Purpose, string Detail, double LatencyMs, string? Problem)
  ```
- UI route `/diag` ONLY (delete other diagnostics pages/routes such as `/system`, `/diagnostics`, `/status`). Page
  `<title>` and `h1`: **System status**. Layout: overall banner in plain words ("Everything is working" /
  "Working, with N things degraded" / "Not working: …"), then three sections in this order: **API**, **Data**,
  **Services**; each row = label, purpose, status pill, latency ms, problem/detail text. Every external dependency
  the app really calls appears as a check. Anonymous, nothing secret in the payload.

#### Workflow (`.github/workflows/deploy.yml`, the only one)
push to master → format check → build (Release, warnings as errors) → Unit tests → Integration tests (Testcontainers /
Azurite; Docker is available on ubuntu-latest) → publish → deploy → confirm `/health/live` X-Commit = this SHA.
If the repo has a MAUI `src/<App>.Mobile`, a second job in the SAME file (`mobile`, `needs: []`, not blocking deploy)
installs the `maui-android` workload and builds the Android target, so the client is always compiled.

#### Tests
- Unit tier: fast, no I/O, runs in CI.
- Integration tier: runs in CI; locally it SKIPS with a clear message when Docker is not running (never a wall of
  `DockerUnavailableException` failures).
- E2E/UI tiers: run locally against a running app; when no app URL / browser is available they SKIP with a clear
  message. They are not part of CI.
- Docs must state real counts, not caps that are out of date.

#### Azure naming
App Service `app-<repo lowercase>`; per-app resources named `<type>-<repo lowercase>`; shared things only in `PoShared`.

## 2. Concurrent work

> Several sessions (people and agents) commit to this repo, sometimes at the same time.
> - Before every commit and every push: `git fetch origin && git rebase --autostash origin/master`.
> - Stage only the files you changed (`git add <paths>`); never `git add -A` / `git add .`; check `git diff --cached`.
> - Never revert, reformat or overwrite changes you didn't make; keep the owner's recent decisions (e.g. features
>   removed on purpose stay removed).
> - Don't commit or sync from the IDE while an agent is working in this folder.
> - Pushing `master` deploys production; changes to `.github/workflows/*` need a token with the `workflow` scope.

## 3. Verification

Every command below was run on 2026-11-21 (PowerShell or bash, repo root).

| What | Command | Needs / skips |
|---|---|---|
| Format | `dotnet format PoWatch.slnx --verify-no-changes` | — |
| Build | `dotnet build PoWatch.slnx -c Release` | warnings are errors (the client is trim-analysed) |
| Hygiene / caps | `pwsh scripts/check-hygiene.ps1` and `pwsh scripts/check-test-caps.ps1` | after a Release build |
| Unit | `dotnet test tests/PoWatch.Unit -c Release` | nothing (58 tests) |
| Integration | `dotnet test tests/PoWatch.Integration -c Release` | Docker (Testcontainers Azurite); skips with one message without it (24) |
| E2E API | `dotnet test tests/PoWatch.E2EAPI -c Release` | Docker; skips without it (14) |
| E2E UI (the release gate) | `pwsh scripts/run-browser-tests.ps1 -Repeat 3` | publishes the trimmed Release build (what deploys), starts Azurite (Docker) and the published app on :7429, runs the tier 3 times in a row against it, stops everything; needs Playwright Chromium (25) |
| E2E UI (quick, untrimmed) | `dotnet test tests/PoWatch.E2EUI -c Release` | hosts the app in-process (Docker) or tests `E2E_BASE_URL`; skips with the reason otherwise |
| Node checks | `node --test scripts/sensory-audio.test.cjs scripts/sensory-visual.test.cjs` | Node |
| Caption eval (syntax) | `node --check scripts/caption-eval/eval.mjs` | Node; a real evaluation is `pwsh scripts/caption-eval.ps1 -Count 5` (installs its npm packages, downloads the models) |
| Azurite for a local run | `docker run -d --rm -p 21000:10000 -p 21001:10001 -p 21002:10002 mcr.microsoft.com/azure-storage/azurite:latest azurite --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0 --skipApiVersionCheck --loose` | Docker |
| Run locally (guest sign-in) | `AzureStorage__ConnectionString="<Azurite connection string, ports 21000-21002>" FeatureFlags__DeveloperBypassAuth=true FeatureFlags__EnableKeyVault=false ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:7428 dotnet run --project src/PoWatch.Api --no-launch-profile` | → http://localhost:7428 and http://localhost:7428/diag |

- **Deploy:** push to `master` runs `.github/workflows/deploy.yml` (format, build, Unit, Integration, publish,
  deploy; the `mobile` job builds the Android client beside it). `*.md` changes do not deploy.
- **Deploy check:** `curl -sI https://app-powatch-win2.azurewebsites.net/health/live` shows `X-Commit: <pushed sha>`
  (the workflow waits for it).
- **Live:** https://app-powatch-win2.azurewebsites.net and https://app-powatch-win2.azurewebsites.net/diag
- **Pinned:** .NET SDK `10.0.400` (`global.json`, rollForward latestFeature); MAUI client built with the
  `maui-android` workload in CI only (not in the .slnx).

---

# PoWatch: repo-specific notes

The pipeline, commands, tests and the easy-to-get-wrong list are in
[docs/development.md](docs/development.md); the overview is [README.md](README.md). Rules for any agent
working in this repository:

### 1. Git & Workflow
* **Stay on `master`:** Commit directly to `master`. Create branches only when specifically asked.
* **Casual Commits:** Write short, natural commit messages using informal American phrasing. Auto-commit and push on sync.

### 2. Running, Testing & Clean Code
* **Run Only Related Tests:** Test only what was touched. Skip tests for minor edits; never run the whole test suite.
* **Zero Warnings:** Fix all build warnings immediately. Treat every warning as a failure.

### 3. Architecture & Setup
* **Keep Docs Updated:** Check `docs/` and `README.md` first to get an overview of the app, and update them in the same change
* **Fully Automated:** Run all builds, scripts, and setup commands directly. Do not ask the user to run terminal commands.
* **Shared Components:** Look to consolidate functionality and UI into shared components when possible. Reusable UI goes in `src/PoWatch.Client/Components/` (`WhoList`, `StatCell`, `Sparkline`, `TerminalPanel`), browser-safe logic in `PoWatch.Shared/Services`; two panels that render the same list or number should share one component rather than copy the markup. `scripts/check-hygiene.ps1` fails on a component nothing renders.

### Radzen is the component library (owner decision 2026-11-21)
Every button, text box, dropdown, number box, checkbox, tab set and chart is a Radzen component
(`Radzen.Blazor`, pinned in `Directory.Packages.props`); class `pw-plain` keeps the app's own look
(docs/development.md, "Radzen controls"). The only native elements left, on purpose:
* **Native video/canvas:** the camera feed `<video>` (`Layout/MainLayout.razor`) and the drawing canvases
  (Live heat map, Journal replay track/heat, time-lapse) - media surfaces, not form controls.
* **The replay scrubber** `<input type="range" data-replay="scrub">` (`Features/Journal/TimelineReplay.razor`):
  the seek bar of the JS-drawn replay (`js/replay.js` owns its value and events every frame), treated like a
  native media control. Recorded as an exception for the owner to confirm.
* **Plain `<form @onsubmit>`** around the weather place search (`WeatherSettings.razor`) - a container; its
  controls are Radzen.

### 4. UI & Output Rules
* **UI Previews:** For visual changes, save before-and-after HTML mockups to `SCREENSHOTS/{YYMMDD}-{feature}.html` / Annotate screenshots and show in a single PNG the before and after with notes
* **Clarifying Questions:** If modifying more than 100 lines and no pending questions exist, list 3 key follow-up questions about future direction or logic.
* **Problems Found:** If there are problems found, propose a solution as well for the user to approve.
* **Required Footers:**
  * **Short Recap:** If your response is over 100 words, add a recap under 20 words at the very end.
  * **3-Level Summary:** If a code change is over 20 lines, add 3 single-sentence summaries and include a diagram to make the changes easier to understand (https://github.com/cathrynlavery/diagram-design):
    * *Toddler:* Explain the idea simply.
    * *Child:* Explain what the app actually does now.
    * *Adult:* Explain how it was built.

## CI/CD and Azure

- A push to `master` runs `.github/workflows/deploy.yml` (the shape in the standard above): format check, Release build, Unit and Integration tiers, publish, deploy, then wait for `/health/live` to report the pushed commit in `X-Commit`. A red check stops the deploy and the live site keeps its last good build. The non-blocking `mobile` job builds the Android target of `src/PoWatch.Mobile`. E2E tiers, Bicep and the other scripts run locally. Nothing is deployed by hand.
- Azure sign-in is GitHub OIDC (the `gh-<repo>-deploy` app). Its IDs are repo **variables** `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID`, and the App Service name is `AZURE_WEBAPP_NAME`. Never move them to secrets or write resource names into workflows.
- Every permission the app and CI need lives in `scripts/ensure-azure-access.ps1`. A role granted by hand and not added there is a bug: the next re-provision loses it.
- The app answers `/health/live` (process up, no dependencies, `X-Commit` header) `/health` (dependencies reachable) and `/api/diag` (the /diag page's report). The deploy waits for `/health/live` to report the pushed commit.
- Tests pass on any machine: no drive-letter, temp-folder or working-directory assumptions, and no live config value hard-coded as an expected result (read it from the same config the app reads).
- A red run on the default branch gets an ntfy push from the CI watcher in PoPunkouterSoftware. Fix it, or say why it is red.

# AGENTS.md

Independent Po web app: this repo owns every file; nothing is synced from another repo. Only Azure resources in `PoShared` may be shared. See [README.md](README.md), [docs/development.md](docs/development.md), and `docs/adr/` for full architecture and pipeline details.

## Core Standards
- **Shell:** `.po-header` (title top-left, signed-in email or Sign in top-right, no settings gear), `.po-version` (`vYYMMDD` from `BuildVersion`) bottom-right (static after `<main>` on phones ≤ 768 px). Zero overlap or horizontal scroll at 390 px. Colours from `po-tokens.css` (≥ 4.5:1 contrast).
- **UI library:** Radzen (`Radzen.Blazor`) with `pw-plain` styling; native media elements only (camera `<video>`, canvases, replay range scrubber).
- **Diagnostics:** `/health/live` (`X-Commit`), `/health`, `/api/diag`, and UI `/diag` ("System status"). Live, cheap, cached probes; services off on purpose show `Off` and don't affect the banner.
- **Privacy & scope:** Observe-only (no Ask box, no naming regulars). Raw face/body landmarks stay on-device; session-level aggregates (`SessionSummaryDto`) save to the session for Journal reports, PDFs, and JSON exports (`docs/adr/0001–0003`).
- **CI/CD:** Single workflow `.github/workflows/deploy.yml` (format, Release build, Unit, Integration, publish, deploy, confirm `X-Commit`, plus non-blocking Android `mobile` job). Hosting is F1 Free (`app-powatch-win2`): never point Unit or Integration test runs at the deployed site `app-powatch-win2` or at shared `PoShared` Azure resources; tests must use local/in-memory or test-only resources.

## Workflow & Verification
- Work and commit directly on the local `master` branch — do not create feature branches or PRs. Before each commit and before each push, run `git fetch origin && git rebase --autostash origin/master`; stage only changed files. Note that every push to `origin/master` triggers `deploy.yml` and deploys to production.
- If the rebase hits conflicts, do not force-resolve or `--skip`; stop, run `git rebase --abort`, and report the conflicting files to the user.
- Fix all warnings immediately (0 warnings). Keep docs updated.
- If a warning comes from generated or third-party code, suppress it with a targeted `#pragma warning disable`/`NoWarn` entry with a comment explaining why; never use global suppressions.


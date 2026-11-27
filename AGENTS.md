# AGENTS.md

Independent Po web app: this repo owns every file; only `PoShared` Azure resources may be shared. See [README.md](README.md), [docs/development.md](docs/development.md), and `docs/adr/`.

## Core Standards
- **Shell:** `.po-header` (title top-left, email/Sign in top-right, no settings gear), `.po-version` (`vYYMMDD`) bottom-right (static after `<main>` on phones ≤ 768 px). Zero overlap or horizontal scroll at 390 px. Colours from `po-tokens.css` (≥ 4.5:1 contrast).
- **UI library:** Radzen (`Radzen.Blazor`) with `pw-plain` styling; native media elements only (camera `<video>`, canvases, replay scrubber).
- **Diagnostics:** `/health/live` (`X-Commit`), `/health`, `/api/diag`, UI `/diag`. Live, cheap, cached probes; off services show `Off` and don't affect the banner.
- **Privacy & People:** Observe-only (no Ask box). Face/body landmarks run on-device; session aggregates (`SessionSummaryDto`) save for Journal/PDF/JSON, and unique people (`PersonProfile`) save in Azure Table Storage and can be named, merged, or deleted on `/people`.
- **CI/CD:** Single workflow `.github/workflows/deploy.yml` (format, Release build, tests, publish, deploy, confirm `X-Commit`). Never test against live `app-powatch-win2` or shared `PoShared` resources.

## Workflow & Verification
- Work on `master`; rebase (`git fetch origin && git rebase --autostash origin/master`) before commit/push; abort and report conflicts.
- Zero warnings (targeted suppressions with comment for generated/3rd-party code only). Keep docs updated.
- Caps: Unit 100, Integration 50, E2E API 25, E2E UI 25 (`scripts/check-test-caps.ps1`, `scripts/check-hygiene.ps1`).

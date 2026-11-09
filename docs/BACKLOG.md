# PoWatch backlog — 50 options

Written 2026-11-09 so the list survives the chat window. Pick by number (e.g. `1, 3, 41`) and an
agent implements them in that order.

Effort: **S** ≤1 day · **M** 2–4 days · **L** a week+. ⭐ = best value for effort.
Status: ✅ shipped · ⚠️ stale when checked against the code on 2026-11-09.

## Features (1–10)

| # | Item | Notes | Effort |
|---|---|---|---|
| 1 ⭐ | Web Push alerts (VAPID) | Watch rules reach a **closed** tab, not just open ones (`POST /api/alerts` relays to open tabs today). Reuses the ntfy pattern from CI. | M |
| 2 ⭐ | ROI zones drawn on the camera | Draw "door", "desk", "couch"; per-zone dwell and visits. Presence is whole-frame today. Unlocks most of stat family A without a new ingest format. | L |
| 3 ✅ | "Today vs usual" on Live | **Shipped:** KEY METRICS is a SESSION vs USUAL table (value, usual mean, σ chip) plus "✦ Explain today", which narrates the flagged anomalies through the fact-guarded Ask path. | S |
| 4 ✅ | Per-class presence timeline | Done — `LiveSensingState` keeps 120 detector passes per presence class and In frame draws the busiest four as sparklines, so a class that leaves falls to zero instead of the line stopping. | S |
| 5 ✅ | Range export | Done — `/api/export.csv?from=&to=` (yyyy-MM-dd) narrows the same year of rows, and the command palette exports the lot. A signed link for someone else is still open. | S |
| 6 | Rolling share `/s/{token}?latest=week` | Shares exist but are frozen to one period. A rolling link makes it a real phone bookmark. | M |
| 7 ✅ | Camera device picker | Done — Settings → Recording lists the video inputs; the choice is stored per browser and used by the next Start (an already-open stream is not swapped, which would restart sensing). A remembered camera that is gone falls back to the front camera. | S |
| 8 | MP4 time-lapse export (WebCodecs) | `timelapse.js` holds frames but only MediaRecorder/WebM ships. H.264 plays everywhere, ~5× smaller. | M |
| 9 ✅ | Outbox health chip | Done — header shows **N queued** (amber, press to send now) and **N lost** (red) only when non-zero; System keeps the full counts. | S |
| 10 | Voice-triggered Ask | Whisper already runs on-device. Needs a wake phrase plus reuse of `AskService`. | M |

## Code cleanup (11–20)

| # | Item | Notes | Effort |
|---|---|---|---|
| 11 ✅ | Delete `Dial.razor` | Done — it was the only component with zero usages, plus its dead CSS (`@property --p`, `.term-dial*`). | S |
| 12 ✅ | Dead-component gate in `check-hygiene.ps1` | Done — every non-`@page` `.razor` must be named as `<Name>`, `typeof(Name)` or `@layout Name`. Proven with a probe component. | S |
| 13 | UI test headroom (19/25) | One more UI feature trips `check-test-caps.ps1`. Merge near-duplicate flows first. | S |
| 14 | Slice alignment | `Api/Features` has `Auth`, `Diagnostics`, `Profile` with no `Application/Features` counterpart. Extract or document. | M |
| 15 | ~~Dead config sweep~~ | **No-op, verified 2026-11-09:** every key in `appsettings*.json` is read (`PoWatch:Ports`, `DevCorsAllowedOrigins`, `KeyVault:Uri`, `ApiBaseUrl`, all sections bound or fetched). | — |
| 16 | ~~Unused package audit~~ | **No-op, verified 2026-11-09:** all 22 `PackageReference`s have a live call site. | — |
| 17 ✅ | Trim gate in CI | Done — the build job publishes the client with `-p:PublishTrimmed=true`, so a reflection-only DTO fails CI instead of someone's browser. | S |
| 18 | Native AOT probe on the API | Throwaway `PublishAot` run; findings either way are worth having. | L |
| 19 | `StatsTabBase` consolidation | `PipelineTab` + `PatternsTab` share fetch/loading; fold into one base with an overridable query. | M |
| 20 | Split `terminal.css` | Live / Journal / Settings get their own `.razor.css` (only `Login.razor.css` exists today); `terminal.css` is ~1,200 lines. | M |

## Graphics & audio (21–30)

Global illumination, RTX and volumetric refraction do not fit a 2D stats dashboard — there is no scene
to light and the cost is real on phones. This is the subset that improves this app.

| # | Item | Notes | Effort |
|---|---|---|---|
| 21 ✅ | HRTF positional entry blips | Done — `PannerNode` (source a little in front and to the side it entered from) with a `StereoPanner` fallback. | S |
| 22 | WebGPU motion compute | The L0 pixel layer reads back frames at 4 Hz on the CPU; a compute pass is the single biggest Live CPU saving. Fall back to canvas2d. | L |
| 23 | Temporal motion-trail shader | `heat-gl.js` draws per-minute grids; accumulate prior grids with decay. | M |
| 24 ✅ | Ambient motion gradient | Done — two very slow radial drifts behind Live as plain CSS (`dash-glow`, 26 s), gone under reduced motion. No shader needed for something this subtle. | S |
| 25 ✅ | Impact-synth cues | Done — `powatchCues.play(name, pan, depth, energy)` scales the gain by the room's motion (0.75× quiet, 1.25× busy), so an entrance lands harder in a busy scene. Pitching by class is still open. | S |
| 26 ✅ | Regular-arrival particle burst | Done — `js/fx.js` (`powatchFx.burst(pan)`): 18 particles from the side a known regular entered, `prefers-reduced-motion` skips it, the RAF loop stops when the last particle dies. | S |
| 27 ✅ | Stillness crossfade | Done — 20 s without a caption dims the feed (`.term-feed.is-still`, 1.2 s transition) so the narration stays readable; it never hides the picture, and it restores the moment something new is seen. | S |
| 28 ✅ | Specular sheen on `TerminalPanel` | Done — a gradient sweep on `.term-panel-head` under `@media (hover: hover) and (pointer: fine)`, off under reduced motion. | S |
| 29 | Sound-level ring | `SoundMean`/`SoundMax` are in every tick; a thin ring pulses at 1 Hz. | S |
| 30 ✅ | WebGPU/WebGL2 capability gate | Done — `theme-controller.js` sets `data-gpu="webgpu\|webgl2\|webgl\|none"` on `<html>` once at boot, before any paint. | S |

## UI/UX optimize (31–40)

| # | Item | Notes | Effort |
|---|---|---|---|
| 31 ⭐ | CRUD punch list with repro steps | Walk 4 Settings tabs, share create/delete, delete-everything, regulars rename/merge, model switching. Severity + repro. Input for 32–36. | M |
| 32 ✅ | Localized retry | Done — keyed `ErrorBoundary` per Settings tab, `panel-error` style, Retry button. | S |
| 33 ⚠️ | ~~Collapse Settings tabs~~ | **Stale:** Settings already has 4 tabs (Recording / People / Alerts / Advanced). Number kept so nothing shifts. | — |
| 34 ✅ | Command palette → real actions | Done — added Journal: today, Journal: this week and Export every observed day as CSV alongside start/stop, demo, tray, settings, System and full screen. Model switching stays in the MODELS strip where you can see the result. | S |
| 35 ✅ | Reserve heights to kill CLS | Done — KEY METRICS reserves 128 px and the SEEN line reserves two lines, so nothing below them jumps when data lands. | S |
| 36 | One toolbar per page | Live stacks AI strip + Ask + Export + session bar. Merge into one header with a drawer. | M |
| 37 | Responsive audit at 768 / 900 / 1100 | The Live grid and Journal index squeeze between card and desktop layouts. | S |
| 38 | Radzen for hand-rolled bits | Radzen 10.4.9 already ships: replace `Drawer`, `ExportMenu`, `AiSelectors`, confirm dialogs. | M |
| 39 ✅ | Build stamp in the corner | Done — `BuildStamp Corner` rendered by `MainLayout`, fixed bottom-right, hidden in print. | S |
| 40 | Prune Live's right column | *In frame* carries four stacked facts; measure, then move "everything seen this session" to a tooltip. | S |

## AI & automation (41–50)

| # | Item | Notes | Effort |
|---|---|---|---|
| 41 ⭐ | AI telemetry rail | TTFT, tokens/s, tokens in/out, retry/fallback path, which provider served. Logged today, never measured. | M |
| 42 ⭐ | `AiFallback` → measured router | Fixed order today (local → Gemini → Azure → Template). Store p50 latency and failure rate per model, order by health, allow "pin to local". | M |
| 43 ✅ | Semantic "find that moment" | **Already shipped:** typing 3+ characters in the command palette searches the caption index by meaning and opens the moment in the Journal (`menus.js` → `powatchCaptionIndex.search`). | — |
| 44 | More Ask tools | Four tools today. Add replay-grid, regulars lookup, weather. | M |
| 45 ✅ | Anomaly narration | **Already shipped:** "✦ Explain today" appears whenever a σ chip is flagged and answers through `AskService`, whose replies are built from the same tools and never invent numbers. | S |
| 46 | Suggested watch rules | Draft 2 rules from the user's history on Settings → Alerts. | M |
| 47 ✅ | Local model manager | Done — when Ollama answers without the chosen caption model, the strip shows a **PULL** button that copies the exact `ollama pull <model>` command. Preload on record start is still open. | S |
| 48 | Detector class/threshold tuning | Sliders in Advanced with a live preview against the current frame. | M |
| 49 | Template-first 5-line session story | `RecapTemplate` writes the numbers; a short bullet summary is the highest-signal LLM addition. | M |
| 50 | Per-user AI budget | Daily calls/tokens cap with silent Template fallback. Protects against a runaway Auto loop. | S |

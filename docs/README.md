# PoWatch — project overview

PoWatch is a stat-heavy webcam app. Point a camera at anything, press **Start**, walk away, and come
back to a nerdy statistical picture of what happened. It is a single-user, hosted app: a Blazor
WebAssembly client served by an ASP.NET Core API on .NET 10.

This page is the source of truth for behavior; the code and tests fill in the detail.

## How it works

```
camera ──► browser (all sensing on-device)                        server
           L0 pixel layer   4 Hz  motion, light, palette, 16×9 grid
           L1 detector      ~1 Hz RF-DETR Nano → boxes → C# tracker  ──►  POST /api/sessions/{id}/batches
           L2 VLM caption   adaptive cadence, plain-sentence prompt       every 10 s: ticks + scene events
                            + MiniLM embedding (novelty, search)
           TickBatcher      10 s ticks, outbox (IndexedDB) + replay       │
                                                                          ▼
                                                  IngestService: raw rows + rollups (minute/hour/day/all)
                                                  ──► SignalR push (statsChanged)
                                                                          │
           Live · History · Regulars   ◄── GET /api/stats/{family}, /api/recaps, …
```

- **Frames never leave the device.** The only images uploaded are highlight snapshots (first sighting
  of a class, motion spikes, notable captions), capped per hour and per day. Time-lapses stay in the
  browser.
- **Ticks, not frames, are the unit of ingest.** A batch has a `batchKey`; the server claims each key
  once, so a replayed batch is stored but never counted twice.
- **Rollups are mergeable.** Every stat query reads pre-aggregated minute/hour/day/all-time rollups
  (count/sum/sum²/min/max, 16×9 grids, class totals, palette and dwell histograms), except the few
  "raw" stats that read at most a day of events.

## Stat families

| Family | What | Where |
|---|---|---|
| A · Presence & space | occupancy, visits, dwell percentiles, peak at once, empty/still streaks, busiest minute, heatmaps, entry/exit edges | Live |
| B · Objects & regulars | classes seen, rarest, regulars leaderboard (recognised by look, never by face) | Live, /regulars |
| C · Patterns & anomalies | hour × weekday, rhythm score, "today vs usual" z-scores, trend, busy-hour forecast | Live |
| D · Environment & captions | light curve, lights on/off, daylight estimate, palette, word cloud, weirdest caption, recaps | Live, History |
| E · Pipeline | frames per layer, FPS, latency, detector confidence, uptime, storage | /system |

## Pages

`/` Live, one screen under one bar (range picker; with Advanced on, an "Ask" box and "JSON ↓" for
every number behind the grid): a 4×4 grid of the camera (2×2, one Start button; a synthetic demo is
available whenever stopped), key metrics for the range beside your usual day (σ, rhythm, trend, next busy hour), who is in
frame with every class seen in the range, then the live motion grid and six stat panels. On a phone the
panels after "In frame" are one swipeable strip · `/history/{yyyy-MM-dd}` on the left the year (with the
weather's effect, once a place is set), the day's numbers, weather and its week against the week
before, and the day's sessions; on the right two tabs (recap with motion and captions, plus JSON/PDF and a
share link with Advanced on; replay, with the day's moments and time-lapse under it); on a desktop each column scrolls on its own · `/regulars` a
card per regular, filterable and sortable: rename in place, tick several to merge (a bar floats up) ·
`/settings` the Settings drawer as a page, for links. Range, session and day live in the URL, so
reloads, links and Back/Forward keep their place. Every page needs a sign-in.

**Header.** Brand, three section keys (a bottom tab bar on a phone), a status chip (state and uptime),
Start/Stop, a bell and a gear. The gear opens Settings in a side drawer over any page: every optional
choice (theme, sound, captions, watch rules), what the server keeps for you (retention window, weather
place, weekly email), your shared recap links, and account and data (System status, CSV export, sign
out, delete everything; keeping less and deleting ask first). The bell is the one notification tray, in
the same kind of drawer: sensing problems, watch-rule alerts, "name this newcomer" prompts and, once a
week, last week in review; it opens by itself for a problem or an alert, and the rest only raise its
count. Escape or ✕ closes a drawer, and so does going to another page. Watch rules ("person between
22:00 and 06:00") are kept in the browser, checked as things enter the frame, and relayed to every open
tab over the hub (plus a desktop notification where allowed).

**Simple by default, Advanced in Settings.** A new browser gets the simple app. Settings' Advanced
switch (`Services/UiPrefs.cs`, remembered per browser) adds the extras: the Ask box, "Explain today"
and JSON on Live; JSON/PDF downloads, share links and the 3D year on History; the caption model,
command palette row, watch rules, weather place, weekly email and shared links in Settings; and the
visual effects (CRT tube switch, comet trails, WebGL2 motion grid). Anything already switched on stays
visible without Advanced (a kept rule, a weather place, a live link). "Reset to defaults" puts this
browser's choices back (dark, low sound, captions on, MiniCPM-V, Advanced off); rules and the
account's server settings stay.

**AI selectors.** The middle of the header has two dropdowns on every signed-in page. Camera AI
chooses MiniCPM-V, Qwen3-VL 8B, Qwen2.5-VL 7B, or captions off; its status reports whether Ollama
answered or the in-browser fallback (WebGPU/WASM) ran, loading/failure, or a pending switch. Changes are saved per browser, also update
Settings, and apply after the current caption finishes, even in a still scene. Ask / Recap AI lists
the server-configured Azure OpenAI, Gemini and Ollama models plus Template. Its choice is kept in an
HTTP-only browser cookie for a year and applies to new Ask, recap, PDF and assist requests, not
other browsers or background digests. Already displayed answers are not regenerated. Labels expose
neither credentials nor endpoints; a configured model can still be unavailable when called.
Template keeps the existing browser recap fallback. On narrow screens the selectors occupy a second
header row.

**Look.** One palette on every page (deep-blue glass in the dark theme, plain panels in the light one),
five type sizes (`--fs-xs` to `--fs-xl`), and Radzen's `--rz-*` variables pointed at the same tokens.

**Keyboard.** `g` then `l h r` jumps to a section, Space starts or stops, ← → step History's
day, Ctrl+K opens a command palette (also: the demo scene, System, full screen) (`wwwroot/js/menus.js`).

**Sound.** `wwwroot/js/cues.js` synthesises short cues with the Web Audio API (start, stop, something
entering — panned to the side it entered from — a new regular, an alert, an error). Level
off/low/mid/high is on Settings and remembered per browser.

**Motion grid.** On Live the 16×9 grid is, with Advanced on, a WebGL2 shader (`wwwroot/js/heat-gl.js`); without WebGL2
or with Advanced off it is the plain grid. Animations use spring easings and all stop under `prefers-reduced-motion`.

**Offline.** The published app registers a service worker that caches the app shell (not the ~20 MB
vision runtime), so PoWatch opens without a network; batches wait in the outbox as before. Offline,
the shell stays open for whoever was last signed in on that browser; the server still decides what
the cookie may read.

**Session recovery.** Accepted ingest batches refresh each running session's last-activity time.
When a user next lists or reads sessions, sessions without activity for ten minutes are closed at
their last observed time. This recovers abandoned sessions; offline batches older than the allowed
session clock skew may no longer be accepted after that timeout.

**Synthetic demo.** The demo is one deterministic smoke-test room, not an AI accuracy test: a
recurring walker, a passing dog, a napping cat, a stationary cup and slowly changing light exercise
the sensing pipeline without a camera or model.

**Tube switch** (`wwwroot/js/crt.js`, Advanced). Start and Stop play a short CRT power-on and power-off over the
camera panel (a transparent WebGL2 overlay, so camera and demo alike), timed with the start and stop
cues; Stop keeps the picture docked until the tube is dark. Nothing plays under reduced motion.

**The year in 3D** (History, `wwwroot/js/skyline.js`, Advanced). The year panel switches between the 2D
calendar and a skyline of instanced WebGL2 towers (height = occupancy, blue = a wet day): drag or
arrow keys to turn, scroll or + − to zoom, hover for the day, click to open it. "Fly over" glides
along the year and plays each week as a note (minor pentatonic, higher when busier). It draws only on
change, never in an idle loop, since the detector and the caption model share the GPU.

**Replay** (History, `wwwroot/js/replay.js`). A day minute by minute from `GET /api/stats/replay`:
a track of the whole day (occupancy bars, motion line, moment ticks, caption dots) under a native
range slider (drag, ← →, Home/End); the playhead drives the motion grid shader, the readout, the
caption of the moment and, where this browser kept a time-lapse, the nearest camera frame. Play walks
the observed minutes only.

**Comet trails** (`wwwroot/js/fx.js`, fed by `Components/FxBridge.razor`, Advanced) follow people and animals over
the camera and add up to a long-exposure PNG on the session recap; they respect
`prefers-reduced-motion`.

## API surface

| Route | Purpose |
|---|---|
| `POST /api/sessions`, `POST /api/sessions/{id}/stop`, `GET /api/sessions[?from=&to=]` | session lifecycle; `from`/`to` list every session overlapping a range |
| `POST /api/sessions/{id}/batches` | tick + event ingest (idempotent by `batchKey`) |
| `GET /api/stats/{presence\|space\|objects\|patterns\|environment\|pipeline}?range=` | stat families |
| `GET /api/stats/all?range=` | every family in one JSON document, plus the raw ticks and events for session, today or one day (Live's "All data ↓", History's "Day data ↓") |
| `GET /api/recaps/session/{id}[.pdf]`, `GET /api/recaps/day/{date}[.pdf]?tz=[&ai=false]` | recaps and PDFs; `ai=false` answers from the template at once |
| `POST /api/ask` | a plain-language question answered from the caller's own statistics (503 without an AI provider) |
| `GET /api/ai/models`, `PUT /api/ai/models` | configured server model labels and the browser's selected model; unknown selections return 400 |
| `POST /api/ask/rule` | a watch rule drafted from a sentence, for the caller to confirm (422 when it is not a rule, 503 without an AI provider) |
| `POST /api/alerts` | relay a fired watch rule to the user's open tabs |
| `GET /api/export.csv`, `DELETE /api/data` | one row per observed day; delete everything stored for the caller |
| `/api/regulars` (+ `/observe`, `/merge`, `PATCH /{id}`), `POST /api/snapshots`, `GET /api/snapshots/read`, `GET /api/sessions/{id}/moments` | regulars and highlights |
| `GET /api/stats/replay?date=&tz=` | one day's observed minutes with their 16×9 motion grids (base64 bytes), and its captions, moments, entrances and exits |
| `GET`/`PUT /api/profile`, `GET /api/places?q=` | retention window, weather place, weekly email; place search (Open-Meteo) |
| `GET /api/weather/year?tz=` | a year of daily weather at the caller's place and, with enough days, how wet days compare with dry |
| `GET /api/digest/week?date=&tz=` | the Monday-to-Sunday week holding a day against the week before |
| `POST`/`GET /api/shares`, `DELETE /api/shares/{token}` | make, list and take down shared recap links (1, 7 or 30 days) |
| `/s/{token}`, `/s/{token}/card.png` (public) | a shared recap as plain HTML (no scripts, `noindex`, strict CSP, `no-store`) and its 1200×630 preview card |
| `/hubs/stats` | SignalR: `statsChanged`, `alert` |
| `/health`, `/health/live`, `/diag/boot` (public), `/api/diagnostics/status` (signed in) | operations |

Unknown `/api/*` routes return 404. Everything except the SPA shell, `/health`, `/diag/boot`, shared
recap pages (`/s/…`) and sign-in requires the BFF cookie (Entra ID, or guest sign-in in Dev/Test).
`/api`, `/auth`, `/hubs` and `/s` are rate limited to 300 requests a minute per signed-in user (per IP
when anonymous).

**Who may sign in.** `AzureAd:AllowedTenants` lists the tenant ids that may; it is compared with the
token's tenant exactly. Left empty, any Microsoft account can sign in and gets its own (empty) data
partition — production logs a warning at startup when that is the case.

## Storage

Azure Table Storage (Azurite locally), partitioned by user: `PoWatchSessions`, `PoWatchTicks`,
`PoWatchSceneEvents`, `PoWatchIngestLedger`, `PoWatchRollups`, `PoWatchRegulars`, `PoWatchProfiles`;
`PoWatchShares` is keyed by token (and deleted by owner on "delete everything").
Blob containers: `snapshots`, `dataprotection-keys`. Storage is required: Azurite locally (docker
compose) and in tests (Testcontainers).

**Retention.** Everything is kept unless the user picks a window (30 days, 90 days or a year) in
Settings. A background job (every 6 h) then deletes that user's raw ticks, scene events, snapshots
and ingest claims older than the window. Rollups are never pruned, so charts, the calendar and
all-time numbers keep the whole history; replays, captions and moments of older days go.

**Weather.** Optional context from Open-Meteo (free, no key): the user picks a place by name; History
shows each day's weather and sun, and once there are three or more wet and dry observed days each,
whether wet days run busier. Older days come from the reanalysis archive (cached a day), the last
week from the forecast API (cached an hour); a failed read leaves the days out.

**Weekly digest.** The week in review is computed on demand from the day rollups (History's "The
week" panel; the bell offers last week once per browser). It is also mailed from Monday 08:00 in the
user's zone when they turn the weekly email on, which needs `Digest:Email:ConnectionString` and
`Digest:Email:Sender` (Azure Communication Services Email) and, for the link back, `Digest:AppUrl`.
Without them the switch is disabled and says why.

**Sharing.** "Share link" on a day's recap freezes it (without moments, whose snapshot links are
private) behind a 128-bit random token for 1, 7 or 30 days. Anyone with the link reads the frozen
copy; the owner can take it down from History or Settings, and "delete everything" removes it.

## Recaps

`TemplateRecap` writes the paragraph from the numbers (Humanizer). If `AiProvider:Provider` names a
model (`AzureOpenAi`, `Gemini` or `Ollama`), an `IChatClient` may rewrite the
paragraph only — never the numbers — and the template wins on timeout, error, or a reply that uses a
number not in the facts (`RecapPrompt.KeepsToFacts`, which reads spelled-out numbers too: "three",
"twenty-one", "twice", "a dozen"). All three are reached through an OpenAI-compatible endpoint by one
SDK (`RecapAi`): Gemini with an AI Studio key (Key Vault secret `Gemini--ApiKey`, model
`gemini-flash-lite-latest` by default), Ollama with none (`llama3.2` on `localhost:11434`, free and
offline). Azure OpenAI signs in with the app's Entra
identity when `AzureOpenAi:ApiKey` is empty (Development uses `gpt-5.4-nano` via the az CLI login;
Production stays on Template until the web app's identity has *Cognitive Services OpenAI User* on
`po-aiservices-shared2`). Replies are cached by prompt, so reopening a day or its PDF costs nothing.
The model answers in a JSON schema (summary + highlights), so there is nothing to parse. Captions,
moment text and names reach the prompt as fenced data with control characters and the fence removed,
and the ingest validator caps their length. History paints the template recap first and swaps in
the model's version when it arrives; where the server has no model, a browser with a built-in one
(Chrome's Prompt API) rewords it locally under the same number check. "Ask PoWatch" uses the same
client with four tools — `get_stats(range)`, `get_day(date)`, `get_patterns()` and
`search_captions(text, days)` — so the numbers in an answer come from the rollups and captions; the
prompt carries today's date, and a question gets at most four tool rounds. Live's "Explain today"
(shown when a σ chip is flagged) asks it what stands out. The same client drafts watch rules from a
sentence (Settings; checked against the 80 detector labels and real hours, then confirmed by the user)
and gives the weekly digest a headline (History's "The week" and the email subject), kept only when
it uses no number the summary lacks (`AssistService`).
QuestPDF renders the PDF; a host without its native engine answers an explained 503.

**Captions.** On this machine, chosen per browser in Settings (`js/caption-models.js`). The worker asks
the local Ollama server (`localhost:11434`) for MiniCPM-V (the default: nearly Qwen3-VL's accuracy at a
third of the time when benchmarked on an RTX 5070 Ti laptop), Qwen3-VL 8B instruct (most detailed) or
Qwen2.5-VL 7B. Ollama allows `http://localhost` origins by default; another origin needs `OLLAMA_ORIGINS`.
When nothing listens there, in-browser Florence-2 base answers (a captioner with a fixed task token; it
cannot take the detector's hint), trying a q4/fp16 mix on WebGPU → fp32 → WASM q8. The Ollama prompt
carries what the detector sees (`Visible: person x2, cat.`), an unchanged scene is re-captioned at most
every 2 minutes, and frames reach the worker as transferred `ImageBitmap`s. What counts as a usable caption is `js/caption-quality.js`.
Each kept caption is embedded (all-MiniLM-L6-v2, int8, on WASM so the GPU stays free); a caption
unlike the last 10 — in meaning when both have embeddings, else in words — becomes a Notable moment
(`HighlightRules.NoveltyMaxCosine`). The vectors stay in this browser (`js/caption-index.js`, IndexedDB,
capped at 20,000; replayed days are added too), and typing three letters into Ctrl+K offers
"Search captions" by meaning; a result opens History's replay on that minute
(`/history/{day}?tab=replay&at={ms}`).

**Detector.** RF-DETR Nano in fp16 on WebGPU and int8 on WASM, each with fp32 behind it; the grey
calibration frame rejects a precision that produces out-of-frame boxes, and one that makes ONNX
Runtime abort (fp16 on Adreno does) is skipped in a fresh worker, since the abort breaks every later
session in the old one. The detector and the caption
model share one GPU, so a WebGPU detector skips its ticks while a caption is being generated
(`window.powatchGpu`). COOP
`same-origin` + COEP `credentialless` make the page cross-origin isolated, so the WASM backend runs
multi-threaded.

## Quality gates

- `TreatWarningsAsErrors`, `dotnet format --verify-no-changes`.
- Test caps (`SCRIPTS/check-test-caps.ps1`): 100 unit · 50 integration · 25 API E2E · 25 UI E2E.
- `SCRIPTS/check-hygiene.ps1`: layout, naming, central package versions, shell assets, and no
  caregiver-era vocabulary.
- CI runs all four suites; UI E2E installs Playwright's Chromium and runs with `E2E_LOCAL=1`.
  Each UI test disposes its page (and isolated browser context), including each viewport-loop
  iteration. Failed sign-in/startup reports browser exceptions, console errors and failed resource
  requests alongside the original assertion, rather than only a missing navigation bar.
- `SCRIPTS/caption-eval.ps1` (by hand, not CI): runs every caption model over 40 COCO indoor frames
  with human captions (or your own `-Frames` folder) through the app's own prompt and rejection
  rules, and reports load time, latency, how many captions were kept and their similarity to the
  references, plus the same-scene/different-scene cosines that novelty has to separate.

## Deploy

Pushing to `master` runs `.github/workflows/deploy.yml` and deploys to production. Resource names are
shared between Bicep and CI in `infra/deployment.json`.

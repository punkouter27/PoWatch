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
           L3 audio         loudness 1 Hz; Whisper words; AudioSet sounds
           TickBatcher      10 s ticks, outbox (IndexedDB) + replay       │
                                                                          ▼
                                                  IngestService: raw rows + rollups (minute/hour/day/all)
                                                  ──► SignalR push (statsChanged)
                                                                          │
           Live · Journal              ◄── GET /api/stats/{family}, /api/recaps, …
```

- **Audio never leaves the device, and is never kept.** The microphone is heard in the browser
  (`js/sensing/audio-layer.js`, `js/audio-worker.js`): a second's loudness goes into each tick
  (`SoundMean`/`SoundMax`), a stretch of speech goes to Whisper (base, English, int8, WASM) and comes
  back as words (a `Speech` scene event), and a loud moment that is not speech goes to an AudioSet
  classifier (AST, int8, WASM) and comes back as a label (a `Sound` event: "Dog", "Music", "Knock").
  Only those leave the browser.
- **Voices stay in this browser.** Each line heard also gets a speaker embedding (WavLM base-plus-sv,
  full precision: about 400 MB, downloaded once on the first line heard; int8 blurred two voices into
  one). A regular's voiceprint (`VoiceBook`) is learned when they are the only person in frame while
  someone speaks, and later speech that sounds like them (cosine ≥ 0.86) is theirs, on camera or not.
  Voiceprints are biometric, so they live in localStorage (`powatch:voices`) and never reach the
  server; only the regular's id travels with the line, so transcripts and stories say "Sam said…".
  Settings' "Who PoWatch knows" marks the regulars whose voice is known, merging keeps a voice, "Forget
  voices" clears them, and "Delete everything" clears them too. Settings' "Listening" switch (on by default, remembered per browser)
  turns it off; the demo room has scripted words and a bark so all of it runs without a microphone.
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
| B · Objects & regulars | classes seen, rarest, regulars leaderboard (recognised by look, never by face; by voice in the browser that learned it) | Live, Settings ("Who PoWatch knows"), the stories |
| C · Patterns & anomalies | hour × weekday, rhythm score, "today vs usual" z-scores, trend, busy-hour forecast | Live (the numbers), the Journal index (the shapes) |
| D · Environment & captions | light curve, lights on/off, daylight estimate, palette, word cloud, weirdest caption, recaps | Live, the Journal |
| E · Pipeline | frames per layer, FPS, latency, detector confidence, uptime, storage | /system |

## Pages

`/` Live, always one session: the one being recorded, until it is stopped, then it stays as the last
session (a session's "Live view", `?session=`, opens another; one that is not yours, or not a session id,
says so and shows your latest). Leaving or reloading the page ends a recording; coming back within two
minutes shows it as saved, unless that was dismissed. After any Stop, Live shows one "Session saved" line
under its bar, in the page's own flow (never over anything, and only on Live): the session's visits,
occupancy and peak, "Open in Journal →", its PDF and ✕; showing it also writes the session's last chapter
and story. Starting a new session clears it. One bar names the session, with an "Ask" box and, once there
is a session, "JSON ↓" for every number behind it. Under it five panels with the camera in the middle (one
Start button; a synthetic demo whenever stopped) and, under the picture, its narration: every caption
(SAW), line of speech (SAID), recognised sound (HEARD), coming and going (IN/OUT) and notable moment,
newest first, a few lines tall and scrolling (`Features/Live/LiveNarration.razor`; while recording it is
the browser's own list in `LiveSensingState.Narration`, after that what the server kept). Around it: key
metrics beside your usual day (σ, rhythm, trend, next busy hour) above, Space down the left (two 16×9
grids, motion now over where people were this session, then where motion and people mostly were, the
busiest door and the entries and exits by edge), and on the right who is in frame with every class seen,
over the room (light curve, palette, caption words). Detector boxes and the "now" counts show only while recording, and visits an hour only once
ten minutes have been observed. A desktop holds the whole grid in the viewport (the camera keeps 16:9);
narrower screens stack it, and a phone turns the panels after "In frame" into a strip you swipe through.
Patterns over days are the Journal's, not Live's ·
`/journal` the Journal, everything that happened: on the left the index (the year, with the weather's
effect once a place is set; links to the week's and month's stories;
every day that has sessions, newest first, with its sessions inside it; "Older sessions" pages back
through the latest hundred; then your usual as shapes: today's curve against the usual day and the last
28 days by hour and weekday), on the right the reader, laid out the same for a day and a session: the
story first, then the replay with the timeline of every line under it · `/journal/{yyyy-MM-dd}` a day
(`/journal` opens on today): the recap (an "Export ▾" menu with its PDF and JSON, and a share link), the day's numbers in
one row, its weather, the week against the week before and the caption words folded under them, the
replay and timeline, then the day's moments and time-lapse · `/journal/s/{id}` one session, where every
session link lands: its story with every chapter (newest first), its numbers, links to its Live view,
its day and an "Export ▾" with its PDF and JSON, and the replay of just its minutes with its timeline · `/journal?view=week|month`
(with `&date=` for the period ◀ ▶ moved to) one story of the whole Monday-to-Sunday week or calendar
month, with the days it was told from · `/settings` the Settings drawer as a page, for links (`?section=`
opens a tab). Session, view and day live in the URL, so
reloads, links and Back/Forward keep their place. On a desktop each Journal column scrolls on its own;
on a phone the reader comes first. Every page needs a sign-in.

**Lines of what happened** (`Services/TimelineEntry.cs`, `Components/MomentLine.razor`). Live's
narration, a session's or a day's timeline, and a day's moments are all the
same line: the time, a tag (SAW · SAID · HEARD · IN · OUT · ★), the sentence ("Sam came in", "Sam: “hello”")
and the snapshot when one was kept. Replay events carry the regular's name (`Who`) for speech, entries
and exits. Recognition lands a moment after someone comes in, so an entrance is named after the fact: on
Live the track's lines are rewritten when its regular is recognised or renamed (`LiveSensingState`), and
the replay gives an unnamed entrance the regular its track was later recognised as (`StatsQueryService`).
In a replay each line is a button that moves the playhead to it, and the playhead lights
the line it is on and scrolls only the list to keep it in view (`replay.js`).

**Header.** One row: brand, two section keys (Live · Journal; a bottom tab bar on a phone),
then a status chip (state and uptime), Start/Stop, a bell, a dark/light button and a gear. The gear opens
Settings in a side drawer over any page, in four tabs: Recording (listening, sound cues, the weather place),
People (who PoWatch knows: rename, filter, sort, merge, forget voices), Alerts (watch rules, the weekly
email, shared recaps) and Advanced, where the niche and the dangerous live: the AI models, this browser
(the command palette, reset), and your data and account (retention window, System status, CSV export,
sign out, delete everything; keeping less and deleting ask first). Old `?section=` names still land:
`sharing` on Alerts, `account` and `look` on Advanced. The bell is the one notification tray, in
the same kind of drawer: sensing problems, watch-rule alerts, "name this newcomer" prompts and, once a
week, last week in review; it opens by itself for a problem or an alert, and the rest only raise its
count. Escape or ✕ closes a drawer, and so does going to another page. Watch rules ("person between
22:00 and 06:00") are kept in the browser, checked as things enter the frame, and relayed to every open
tab over the hub (plus a desktop notification where allowed).

**One mode.** There is no simple/advanced switch: Ask, "Explain today" and JSON on Live, the Export
menus and share links in the Journal, watch rules, the weather place, the weekly email and shared
links in Settings are always there. "Reset to defaults" puts this browser's choices back (dark, low
sound, captions on, MiniCPM-V); rules and the account's server settings stay.

**Downloads that can fail.** PDFs are made on the server, so their links (the Journal's Export menus, the
"Session saved" line) fetch first and save only a real file (`wwwroot/js/files.js`); a failure says why in a
toast instead of saving an error as a `.pdf`. The bell's "Watch rules" and "Name everyone at once" links open
Settings on the Alerts and People tabs.

**Saving the words.** Live's narration and every timeline in the Journal (a day's or a session's, as
filtered) have a "Text ↓" link: the lines as a plain-text file, oldest first, one per line ("2026-11-01
21:53:06  SAW   A person walks across the room."). The text rides in the link as a `data:` URL
(`Components/TranscriptLink.razor`, `TimelineEntry.Transcript`), so saving needs no script or request.

**AI models** (Settings → Advanced). Two dropdowns. Camera AI
chooses MiniCPM-V, Qwen3-VL 8B, Qwen2.5-VL 7B, or captions off; its status reports whether Ollama
answered or the in-browser fallback (WebGPU/WASM) ran, loading/failure, or a pending switch. Changes are saved per browser, also update
Settings, and apply after the current caption finishes, even in a still scene. Ask / Recap AI lists
the server-configured Azure OpenAI, Gemini and Ollama models plus Template and, when any model is
configured, Auto: the local Ollama model when it lists the configured model (probed with a one-second
timeout, the answer kept 30 seconds), else Gemini, else Azure OpenAI, else Template; its label names
the model it resolves to. `AiProvider:Provider` picks the default (Auto in Development, Template in
Production and tests). Its choice is kept in an
HTTP-only browser cookie for a year and applies to new Ask, recap, PDF and assist requests, not
other browsers or background digests. Already displayed answers are not regenerated. Labels expose
neither credentials nor endpoints; a configured model can still be unavailable when called, and then
the next one in Auto's order is tried once (`AiFallback`) before the recap falls back to the template
or Ask answers its usual 503; the log says which model answered.
Template keeps the existing browser recap fallback. On narrow screens the selectors occupy a second
header row.

**Safety nets.** Every panel (`TerminalPanel`) has its own error boundary, so a failing
panel shows a small retry box while the rest of the page keeps working; the page-level boundary in
`MainLayout` stays as the last resort. Empty days and an empty story feed offer "Try the
demo" (`?start=demo`). The yyMMdd build date sits in the bottom-right corner (`BuildStamp`, from an
`AssemblyMetadata` item in the client csproj). System is three tabs (Connections, Engine, Pipeline), and
tables marked `term-table--cards` turn into one card per row on a phone.

**Look.** One palette on every page (deep-blue glass in the dark theme, plain panels in the light one),
five type sizes (`--fs-xs` to `--fs-xl`), and Radzen's `--rz-*` variables pointed at the same tokens.

**Keyboard.** `g` then `l` or `j` jumps to Live or the Journal, Space starts or stops, ← → step the
Journal's day, Ctrl+K opens a command palette (also: the demo scene, System, full screen) (`wwwroot/js/menus.js`).

**Effects.** The header is frosted glass. WebGL canvases give their context back when their page goes away.

**Sound.** `wwwroot/js/cues.js` synthesises short cues with the Web Audio API (start, stop, something
entering — panned to the side it entered from — a new regular, an alert, an error). Level
off/low/mid/high is on Settings and remembered per browser. That is all of it: one entry blip for
anything, no hum, no replay ticks.

**Motion grid.** On Live the 16×9 grid is a plain grid of cells; the Journal's replay draws it with a
WebGL2 shader (`wwwroot/js/heat-gl.js`), falling back to the plain grid without WebGL2. Animations use
spring easings and all stop under `prefers-reduced-motion`.

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

**Replay** (the Journal, `Features/Journal/TimelineReplay.razor`, `wwwroot/js/replay.js`). A day, or one session of it, minute by minute from `GET /api/stats/replay`:
a track of the whole day (occupancy bars, motion line, moment ticks, caption dots) under a native
range slider (drag, ← →, Home/End); the playhead drives the motion grid shader, the readout, the
caption of the moment and, where this browser kept a time-lapse, the nearest camera frame. Play walks
the observed minutes only.

**Time-lapse ↓** (the Journal, under a day's replay) encodes the time-lapse this browser kept for the day to WebM
(MediaRecorder) and downloads it. The frames live only in the browser that recorded them, so anywhere
else the button is not shown.

**Interop.** Replay's 16×9 grids reach JS as raw bytes through `[JSImport]` (`Services/ReplayGrids.cs`,
`js/replay-grids.js`), and detector boxes reach the C# tracker as typed arrays through `[JSExport]`
(`SensingSession.OnDetectorBoxes`), colour signatures included.

**Client layout.** `PoWatch.Client` is grouped by feature: `Features/Live`, `Journal` (the index and the reader for days, sessions and periods),
`Diagnostics` (the `/system` page), `Settings` and `Account`; `Layout` is the shell, `Components` holds
the parts shared between features, `Services` the state and API client.

**Server layout.** The server is sliced the same way: each feature has a folder under `Api/Features`
(endpoints) and, when it has logic, one of the same name under `Application/Features` (its services,
namespace `PoWatch.Application.Features.{Name}`): Ask, Data (retention), Digest, Ingest, Recaps, Regulars,
Sessions, Shares, Stats, Stories, Weather. What every feature shares sits at the Application root
(`AiFallback`, `ApplicationJson`), and the storage ports in `Application/Contracts`.

## API surface

| Route | Purpose |
|---|---|
| `POST /api/sessions`, `POST /api/sessions/{id}/stop`, `GET /api/sessions[?from=&to=]` | session lifecycle; `from`/`to` list every session overlapping a range |
| `POST /api/sessions/{id}/batches` | tick + event ingest (idempotent by `batchKey`) |
| `GET /api/stats/{presence\|space\|objects\|patterns\|environment\|pipeline}?range=` | stat families |
| `GET /api/stats/all?range=` | every family in one JSON document, plus the raw ticks and events for session, today or one day (Live's "JSON ↓", the Journal's day "JSON ↓") |
| `GET /api/recaps/session/{id}.pdf`, `GET /api/recaps/day/{date}[.pdf]?tz=[&ai=false]` | recaps and PDFs; `ai=false` answers from the template at once |
| `GET /api/sessions/{id}/transcript` | every line the on-device speech model heard in the session |
| `GET /api/stories/{week\|month}?date=&tz=` | the story of the week or month holding a day, told from its days |
| `POST /api/sessions/{id}/story` | write the session's closed ten-minute chapters and, once it has ended, its story; returns both |
| `POST /api/ask` | a plain-language question answered from the caller's own statistics (503 without an AI provider) |
| `GET /api/ai/models`, `PUT /api/ai/models` | configured server model labels (Auto's names the model it resolves to now) and the browser's selected model; unknown selections return 400 |
| `POST /api/ask/rule` | a watch rule drafted from a sentence, for the caller to confirm (422 when it is not a rule, 503 without an AI provider) |
| `POST /api/alerts` | relay a fired watch rule to the user's open tabs |
| `GET /api/export.csv`, `DELETE /api/data` | one row per observed day; delete everything stored for the caller |
| `/api/regulars` (+ `/observe`, `/merge`, `PATCH /{id}`), `POST /api/snapshots`, `GET /api/sessions/{id}/moments` | regulars and highlights (moments carry their own short-lived image links) |
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
`PoWatchSceneEvents`, `PoWatchIngestLedger`, `PoWatchRollups`, `PoWatchRegulars`, `PoWatchProfiles`,
`PoWatchStories` (row `{session}|c0003` per chapter, `{session}|story`);
`PoWatchShares` is keyed by token (and deleted by owner on "delete everything").
Blob containers: `snapshots`, `dataprotection-keys`. Storage is required: Azurite locally (docker
compose) and in tests (Testcontainers).

**Retention.** Everything is kept unless the user picks a window (30 days, 90 days or a year) in
Settings. A background job (every 6 h) then deletes that user's raw ticks, scene events, snapshots
and ingest claims older than the window. Rollups are never pruned, so charts, the calendar and
all-time numbers keep the whole history; replays, captions and moments of older days go.

**Weather.** Optional context from Open-Meteo (free, no key): the user picks a place by name; the Journal
shows each day's weather and sun, and once there are three or more wet and dry observed days each,
whether wet days run busier. Older days come from the reanalysis archive (cached a day), the last
week from the forecast API (cached an hour); a failed read leaves the days out.

**Weekly digest.** The week in review is computed on demand from the day rollups (the Journal's "The
week" panel; the bell offers last week once per browser). It is also mailed from Monday 08:00 in the
user's zone when they turn the weekly email on, which needs `Digest:Email:ConnectionString` and
`Digest:Email:Sender` (Azure Communication Services Email) and, for the link back, `Digest:AppUrl`.
Without them the switch is disabled and says why.

**Sharing.** "Share link" on a day's recap freezes it (without moments, whose snapshot links are
private) behind a 128-bit random token for 1, 7 or 30 days. Anyone with the link reads the frozen
copy; the owner can take it down from the Journal or Settings, and "delete everything" removes it.

## Recaps

`TemplateRecap` writes the paragraph as a story of the scene (`SceneNarrator`): who came in and from
which side (regulars by name), what the caption model saw, what was said and which sounds were heard,
who left, when the lights changed, then how it felt from the telemetry (light and its colour, the
liveliest moment and the longest still stretch, where in the frame things moved, how loud it was, how
many were in view at once, who stayed longest), with
times as the only numbers and a long day cut to its six busiest stretches. A window without scene
events (seeded history) gets the numbers paragraph instead (Humanizer); the numbers themselves are
always in the recap's grid and highlights. If `AiProvider:Provider` names a
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
and the ingest validator caps their length. The Journal paints the template recap first and swaps in
the model's version when it arrives; where the server has no model, a browser with a built-in one
(Chrome's Prompt API) rewords it locally under the same number check. "Ask PoWatch" uses the same
client with four tools — `get_stats(range)`, `get_day(date)`, `get_patterns()` and
`search_captions(text, days)` — so the numbers in an answer come from the rollups and captions; the
prompt carries today's date, and a question gets at most four tool rounds. Live's "Explain today"
(shown when a σ chip is flagged) asks it what stands out. The same client drafts watch rules from a
sentence (Settings; checked against the 80 detector labels and real hours, then confirmed by the user)
and gives the weekly digest a headline (the Journal's "The week" and the email subject), kept only when
it uses no number the summary lacks (`AssistService`).
QuestPDF renders the PDF; a host without its native engine answers an explained 503.

**Heard.** Live's narration shows every line of speech (SAID) and recognised sound (HEARD) as it comes,
and a session's or a day's timeline in the Journal has them word for word, beside the captions, apart
from the story they feed (`GET /api/sessions/{id}/transcript` still lists a session's speech alone).

**Session stories** (`StoryService`). Every ten minutes of a session is a chapter: a recap of just that
stretch (whole-minute rollups, its moments), the model's version where one answers. While a session
runs, the browser asks for the next chapter every ten minutes (`SensingSession`, 30 s after each mark
so the last batches have landed). Once the session has ended, its short last chapter is written and
the chapters are joined into one story, in order: by the model under the same number check
(`RecapPrompt.StorySystem`), or else the chapters with their times, quiet chapters left out. Each call
writes only what has fallen due, so a missed one is caught up by the next; a backlog (a sleeping tab,
a session closed as stale) is written from the template, with the model kept for the newest chapter.
Models are asked for a story of the scene, not statistics, under the same number check. The story
shows in the Journal's reader for the session (Live's "Session saved" line asks for it after Stop)
(one chapter is not listed again under the story it already is); each asks for
it, so a session closed without Stop gets its story the first time someone opens it. Stories are kept
like rollups: retention does not prune them, "delete everything" does.

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
"Search captions" by meaning; a result opens the Journal's replay on that minute
(`/journal/{day}?at={ms}`).

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
- A pre-commit hook (`SCRIPTS/hooks/pre-commit`, installed by `SCRIPTS/setup.ps1` through
  `core.hooksPath`) runs the format check on the staged C# files (`POWATCH_HOOK_FULL=1` for the whole
  solution) and `check-hygiene.ps1`.
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

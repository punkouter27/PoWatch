# Body readings stay on the device; session aggregates save to Journal

The owner asked for every limb to be tracked when a whole body is in view, and for what that shows:
what each person is doing, how upright they sit, how restless they are, whether their left and right
sides move evenly, and their body language. A second pose model already ran (ViTPose+ inside the
detector worker, 17 points, sitting/standing/lying only), but it has no hands, feet or depth, so
MediaPipe's **Pose Landmarker** (lite, 33 points with a metre-scale "world" copy) runs beside it from
the runtime already vendored for faces; only its ~6 MB `.task` model is downloaded.

Like **face readings** (ADR 0001), raw body landmarks stay on the device and never leave it. A body
is "Body 1" for as long as it is followed from frame to frame and is never linked to a face label
or a **Regular**. Following the owner's 2026-11-27 request to analyse all captured session data
alongside actual images in Journal reports, **session-level aggregates only** (posture score,
slumped seconds, limb energy, activity totals, left/right balance, shoulder tilt, and body-language
cue durations) are saved on the session record (`SessionSummaryDto` via `PUT /api/sessions/{id}/summary`)
and included in Journal reports, PDF recaps, and JSON exports — matching ADR 0001's aggregates-only rule.

Sitting and standing still come from ViTPose (stored in ticks and rolled up by day); the break nudge
reads that same streak. The thresholds (a neck bent 30° from the trunk, a trunk 25° from vertical,
8 cm of lean) are rules of thumb, said as such in the UI, not clinical measures.

# Body readings stay on the device and last one session

The owner asked for every limb to be tracked when a whole body is in view, and for what that shows:
what each person is doing, how upright they sit, how restless they are, whether their left and right
sides move evenly, and their body language. A second pose model already ran (ViTPose+ inside the
detector worker, 17 points, sitting/standing/lying only), but it has no hands, feet or depth, so
MediaPipe's **Pose Landmarker** (lite, 33 points with a metre-scale "world" copy) runs beside it from
the runtime already vendored for faces; only its ~6 MB `.task` model is downloaded.

Like **face readings** (ADR 0001), the points are read on the device and never leave it. Unlike them,
nothing about a body is stored at all, not even aggregates: `BodyReadings` holds person-time totals,
an energy history and current readouts in memory for the session, and a new session starts empty. A
body is "Body 1" for as long as it is followed from frame to frame and is never linked to a face label
or a **Regular**. Sitting and standing still come from ViTPose (they are stored in ticks and roll up
by day); the break nudge reads that same streak.

The thresholds (a neck bent 30° from the trunk, a trunk 25° from vertical, 8 cm of lean) are rules of
thumb, said as such in the UI, not clinical measures. Rejected: replacing ViTPose (it feeds stored
posture rollups that would change meaning) and storing the body numbers in ticks (the owner asked for
something to look at, not another stored series; it can be added later through ADR 0001's rule).

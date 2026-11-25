# Face statistics are computed on the device and stored only as aggregates

The repo said "nothing biometric" in three places — `RegularMatcher`, the detector's appearance
signature and the voice book — and the app's premise is that it observes without identifying. A face
request (landmarks scored for age, presentation and traditional beauty) crossed that line, so it was
decided deliberately rather than by accident: face landmarks and the numbers read from them are
computed in the browser or on the phone and only **aggregates** reach the tick — face-seconds, counts
per bucket, a mean score. No face embedding, no per-face row, and no link from a face to a
**Regular** is ever computed or stored, so "observes, does not identify" still holds for what leaves
the device. The rejected alternatives were storing trajectory-level face rows (re-identifiable) and a
live-only overlay (no statistics at all, which is not what this app is for).

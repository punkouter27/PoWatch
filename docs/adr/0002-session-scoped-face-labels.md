# Face identity lasts one session and never leaves memory

Telling people apart by their face is identification, which ADR 0001 forbade outright and the app's
premise had refused since 2026-11-23. The owner asked for it anyway — faces told apart and given
temporary number labels — so it is recorded rather than done quietly: a face model's descriptor is
held **only in memory**, by one `FaceLabeler` per session, and the longest it lives is the session.
Nothing about it is written to a tick, a rollup, storage, an export or a log, it is never matched
across sessions, and it never touches a **Regular**, so a person is "Person 3" for as long as the
camera runs and a stranger to the app the next time. Matching happens on a running mean with a cosine
distance of 0.35, and the session labels at most 20 people. The rejected alternatives were storing
descriptors alongside **Regular** (a permanent biometric gallery, which is the thing this repo has
always refused) and labelling nothing (which is not what was asked for).

Amended 2026-11-25, when a model was wired up: no face-recognition model ships for the browser from a
source this repo trusts, so the descriptor is DINOv2-small's view of a face crop, a general image
model. On 40 LFW photos it could not separate people on its own (half of same-person pairs were past
0.39, 5% of different-person pairs within 0.3), so a face keeps its label by being **followed** from
frame to frame, and the descriptor only decides whether a new face is someone who came back (0.2,
excluding labels already in view) or whether a followed spot now holds someone else (past 0.45). A
miss gives a returning person a new number; it never gives them someone else's.

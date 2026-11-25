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

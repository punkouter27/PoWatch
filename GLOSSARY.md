# PoWatch

A stat-heavy webcam observer. It watches a space, records what happened as folds — presence, motion,
objects, posture, light, sound — and shows the numbers back. It observes; it does not identify or
converse, and the sensing runs on the device that holds the camera.

## Language

**Regular**:
A person, pet or object recognised across sessions by look alone, and named only by position — "Person 3", "Cat 1".
_Avoid_: Known face, known person, identity, profile

**Keypoint**:
One of the 17 body joints the pose model returns, used to read **posture**.
_Avoid_: Joint, pose point

**Landmark**:
One of the 68 points of the iBUG layout on a face, read from the face model's 478-point mesh; the raw material for a **traditional beauty** score.
_Avoid_: Keypoint, facial point, face print

**Face reading**:
The scoring of one face in one frame from its **landmarks**. It happens on the device, and only its numbers leave the page.
_Avoid_: Scan, analysis, biometric profile, face recognition

**Face descriptor**:
The numbers a face model makes from one face, used only to tell that face from another. It is held in memory for one session and never written anywhere.
_Avoid_: Template, fingerprint, face print, biometric hash

**Face label**:
The temporary "Person 3" a face carries for the length of one session, from a **face descriptor**. It is not a **Regular**, it is not stored, and the next session starts with nobody known.
_Avoid_: Identity, account, name, known face, profile

**Traditional beauty**:
How closely a face sits to the classical canons of proportion — symmetry, the facial thirds, the rule of fifths and the golden-ratio spans — as geometry, 0–100. It is a statement about proportions, not about a person.
_Avoid_: Attractiveness score, beauty AI, hotness, rating

**Pose**:
How a body is arranged as read from **keypoints** — standing, sitting or lying.
_Avoid_: Posture (that is the reading, not the arrangement)

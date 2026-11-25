---
name: grill-with-docs
description: A relentless interview to sharpen a plan or design, which also writes the project's docs (a glossary and ADRs) as it goes. Use only when the user asks to grill a plan or design with docs (/grill-with-docs); do not start it on your own.
---

Load the `grilling` and `domain-modeling` skills with the `skill` tool, one call each. Both are required: `grilling` supplies the interview, `domain-modeling` supplies the writing. If either does not load, say so instead of improvising an interview.

Then run a single session that follows both:

- Interview in `grilling` rounds: the whole frontier, every question numbered with your recommendation on a `➡️` line, then wait. Facts come from the codebase (read it, or dispatch a search), never from the user. Decisions are the user's; never answer one yourself.
- Write with `domain-modeling` as terms and decisions settle, not in a batch at the end:
  - A resolved term goes into `GLOSSARY.md` at the repo root immediately, using [GLOSSARY-FORMAT.md](../domain-modeling/GLOSSARY-FORMAT.md).
  - A decision that is hard to reverse, surprising without context and the result of a real trade-off is offered as an ADR in `docs/adr/`, using [ADR-FORMAT.md](../domain-modeling/ADR-FORMAT.md). Most sessions earn none.
  - Everything else you agreed stays in the conversation.
- Cross-reference what the user says against the code and the committed glossary, and say out loud when the two disagree.
- `GLOSSARY.md` is a glossary and nothing else: no implementation detail, no spec, no scratch notes.
- Stop when the frontier is empty and ask the user to confirm the understanding is shared. Do not start building in this session.

This repo keeps its prose in `docs/` (lowercase) and at the two root files `README.md` and `AGENTS.md`; `GLOSSARY.md` and `docs/adr/` are the only extra docs this skill creates, and it creates them lazily.

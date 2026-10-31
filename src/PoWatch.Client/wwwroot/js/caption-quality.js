// caption-quality.js
// Decides whether a caption model's reply describes the scene. Shared by inference-worker.js and the
// offline caption eval (SCRIPTS/caption-eval), so the harness scores exactly what the app keeps.
// Pure functions, no DOM, no transformers.js.

// Words that say nothing about the scene (Florence-2 answers "unanswerable" to a frame it cannot read),
// and the signature of a small model stuck in a loop.
const DENY = new Set(['yes', 'no', 'ok', 'yeah', 'yep', 'nope', 'none', 'true', 'false', 'maybe', 'unanswerable', 'unknown']);

function hasRepetition(text) {
  const counts = Object.create(null);
  for (const w of text.toLowerCase().split(/\s+/)) {
    if (w.length < 2) continue;
    counts[w] = (counts[w] ?? 0) + 1;
    if (counts[w] > 3) return true;
  }
  return /(.{3,})\1{3,}/i.test(text);
}

/**
 * Judges one reply. Returns { ok: true, activity, caption } or { ok: false, reason } where reason is
 * one of 'empty', 'low-quality', 'echo', 'incomplete' (the eval counts them by reason).
 */
export function judgeCaption(output, prompt) {
  const text = (output ?? '').replace(/\s+/g, ' ').trim();
  if (text.length === 0) return { ok: false, reason: 'empty' };

  // The prompt asks for one plain sentence; the C# caption parser keeps the first one. Here only the
  // replies that are clearly not a description are dropped.
  const activity = text.split(/[.\n]/)[0].trim().slice(0, 80);
  if (activity.length < 6 || DENY.has(activity.toLowerCase()) || hasRepetition(activity)) {
    return { ok: false, reason: 'low-quality' };
  }

  // Small VLMs copy instruction text verbatim; a fabricated record that reads plausibly is worse than a
  // rejected one. Only the instruction line counts: words from the "Visible:" hint are meant to be reused.
  const instruction = (prompt ?? '').split('\n')[0].toLowerCase();
  const promptWords = new Set(instruction.match(/[a-z]{4,}/g) ?? []);
  const activityWords = activity.toLowerCase().match(/[a-z]{4,}/g) ?? [];
  const echoed = activityWords.filter((w) => promptWords.has(w)).length;
  if (/[<>]/.test(activity) || (activityWords.length >= 3 && echoed / activityWords.length >= 0.8)) {
    return { ok: false, reason: 'echo' };
  }

  // A sentence ending on a bare article or preposition ran out of tokens mid-thought.
  if (/\b(?:is|has|in|at|of|on|with)\s+(?:a|an|the)\s*\.?\s*$|\ba\s+bit\s*\.?\s*$/i.test(activity)) {
    return { ok: false, reason: 'incomplete' };
  }

  return { ok: true, activity, caption: text.slice(0, 200) };
}

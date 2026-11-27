// On-device spoken announcements (Web Speech Synthesis) for watch rules, named arrivals, and sit breaks.
(() => {
  'use strict';
  let enabled = false;
  let lastSpoken = '';
  let lastAt = 0;

  function setEnabled(on) {
    enabled = Boolean(on);
    if (!enabled && globalThis.speechSynthesis) {
      try { globalThis.speechSynthesis.cancel(); } catch { /* ignore */ }
    }
  }

  function speak(text) {
    if (!enabled || !text || !globalThis.speechSynthesis) return;
    const now = Date.now();
    if (text === lastSpoken && now - lastAt < 8000) return;
    lastSpoken = text;
    lastAt = now;
    try {
      const utter = new SpeechSynthesisUtterance(text);
      utter.rate = 1.04;
      utter.pitch = 1.0;
      utter.volume = 0.85;
      globalThis.speechSynthesis.speak(utter);
    } catch { /* speech synthesis unavailable */ }
  }

  window.powatchTts = { speak, setEnabled, isEnabled: () => enabled };
})();

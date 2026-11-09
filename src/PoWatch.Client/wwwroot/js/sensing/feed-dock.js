// Keeps the camera <video> alive across pages. The element lives in the layout (so navigating away
// from Live never removes it — Chrome pauses a video the moment it leaves the document) and is
// positioned over whichever "slot" the Live page offers. With no slot it is hidden but still playing,
// so the sensing session keeps sampling while you look at Stats.
(function () {
  'use strict';

  let _video = null;
  let _slot = null;
  let _observer = null;

  function place() {
    if (!_video || !_slot || !_slot.isConnected) return;
    const r = _slot.getBoundingClientRect();
    const main = document.getElementById('main-content');
    const m = main ? main.getBoundingClientRect() : { top: 0, left: 0, right: innerWidth, bottom: innerHeight };
    Object.assign(_video.style, {
      left: `${r.left}px`,
      top: `${r.top}px`,
      width: `${r.width}px`,
      height: `${r.height}px`,
      // Clip to the scrolling content area so the feed never slides over the header or the tape.
      clipPath: `inset(${Math.max(0, m.top - r.top)}px ${Math.max(0, r.right - m.right)}px ${Math.max(0, r.bottom - m.bottom)}px ${Math.max(0, m.left - r.left)}px)`,
      visibility: 'visible',
    });
  }

  // Page resize and scroll only move the feed while it is docked, so the listeners live that long.
  function dock(video, slot) {
    _video = video;
    _slot = slot;
    _observer?.disconnect();
    _observer = new ResizeObserver(place);
    _observer.observe(slot);
    addEventListener('resize', place);
    addEventListener('scroll', place, true);
    place();
  }

  function undock() {
    _slot = null;
    _observer?.disconnect();
    _observer = null;
    removeEventListener('resize', place);
    removeEventListener('scroll', place, true);
    if (_video) _video.style.visibility = 'hidden';
  }

  // "While you were away": tell .NET when the tab comes back after being hidden for a while.
  let _hiddenAt = null;
  let _returnRef = null;
  let _returnAfterSeconds = 300;
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) { _hiddenAt = Date.now(); return; }
    if (_hiddenAt && _returnRef && (Date.now() - _hiddenAt) / 1000 >= _returnAfterSeconds) {
      _returnRef.invokeMethodAsync('OnReturned', Math.round((Date.now() - _hiddenAt) / 1000)).catch(() => { });
    }
    _hiddenAt = null;
  });

  function watchReturn(dotnetRef, afterSeconds) {
    _returnRef = dotnetRef;
    _returnAfterSeconds = afterSeconds || 300;
  }

  function unwatchReturn() { _returnRef = null; }

  // A scene that has stopped changing dims a little, so the words beside it stay easy to read.
  function still(on) {
    if (_video) _video.classList.toggle('is-still', !!on);
  }

  window.powatchFeed = { dock, undock, watchReturn, unwatchReturn, still };
})();

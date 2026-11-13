// The webcam: which one opens, the stream itself and the preview element it plays in.
//
// Settings lists the devices and the choice is remembered per browser; the next Start uses it. A
// stream that is already open is not swapped mid-session (that would restart the whole sensing
// pipeline), so pick before pressing Start. Empty means the front camera, which is what a laptop has.
// The caption bridge (inference-bridge.js) opens and closes the stream; .NET calls list, selected and select.
(() => {
  'use strict';
  const KEY = 'powatch:camera';
  const SIZE = { width: { ideal: 1280 }, height: { ideal: 720 } };

  let _stream = null;
  let _preview = null;

  /** "id<TAB>label" per line: the client splits text instead of handing a device list through JSON. */
  async function list() {
    try {
      if (!navigator?.mediaDevices?.enumerateDevices) return '';
      const all = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput');
      return all.map((d, i) => `${d.deviceId}\t${d.label || `Camera ${i + 1}`}`).join('\n');
    } catch { return ''; }
  }

  function selected() {
    try { return localStorage.getItem(KEY) || ''; } catch { return ''; }
  }

  function select(id) {
    try { id ? localStorage.setItem(KEY, id) : localStorage.removeItem(KEY); } catch { /* private mode */ }
    return selected();
  }

  async function openStream() {
    const id = selected();
    const front = { ...SIZE, facingMode: 'user' };
    try {
      return await navigator.mediaDevices.getUserMedia({ audio: false, video: id ? { ...SIZE, deviceId: { exact: id } } : front });
    } catch (e) {
      // A remembered camera that is gone (unplugged, another dock) must not leave you with no camera.
      if (e?.name === 'OverconstrainedError' && id) {
        select('');
        return await navigator.mediaDevices.getUserMedia({ audio: false, video: front });
      }
      throw e;
    }
  }

  /** Opens the camera if it is not open yet; resolves to 'OK' or what went wrong and what to do about it. */
  async function open() {
    if (!navigator?.mediaDevices?.getUserMedia) {
      return 'This browser has no camera access. Try another browser, or use Demo scene.';
    }
    try {
      _stream ??= await openStream();
      return 'OK';
    } catch (e) {
      // Nothing falls back to a preview: say what went wrong and what to do about it.
      const why = {
        NotReadableError: 'The camera is in use by another app or tab. Close it (or restart Windows if nothing else is open) and press Start camera again.',
        NotAllowedError: 'Camera permission was denied. Allow it in the address bar or in Windows camera privacy settings.',
        NotFoundError: 'No camera was found on this device.',
        NotSupportedError: 'This browser cannot use a camera here.',
        OverconstrainedError: 'The camera could not be opened at a usable size.',
      }[e?.name] ?? 'The camera could not be started.';
      return `${why} The demo (“No camera? Try the demo”) works without one.`;
    }
  }

  /** Plays the open stream in `videoElement`, which becomes the preview. */
  async function attach(videoElement) {
    if (!videoElement || !_stream) return;
    if (videoElement.srcObject !== _stream) videoElement.srcObject = _stream;
    videoElement.muted = true;
    videoElement.playsInline = true;
    _preview = videoElement;
    try {
      await videoElement.play();
    } catch {
      // Ignore autoplay timing issues; the stream remains attached.
    }
  }

  /** Stops the camera and empties the preview. */
  function close() {
    if (_preview) {
      _preview.pause();
      _preview.srcObject = null;
      _preview = null;
    }
    for (const track of _stream?.getTracks() ?? []) track.stop();
    _stream = null;
  }

  window.powatchCamera = { list, selected, select, open, attach, close, isOpen: () => _stream !== null };
})();

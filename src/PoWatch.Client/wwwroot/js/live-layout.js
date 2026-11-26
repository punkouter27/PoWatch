// Drag and drop for Live's panels. A panel is dragged by the grip in its title bar (so text in the panel
// can still be selected), the whole panel is the drag image, and dropping it on another panel tells .NET
// to move it there (insert and shift). Keyboard and touch users move panels from the Panels menu instead.
(function () {
  'use strict';

  let _grid = null;
  let _ref = null;
  let _dragged = null;

  const slotOf = (el) => (el instanceof Element ? el.closest('.live-slot') : null);

  function clearTargets() {
    _grid?.querySelectorAll('.live-slot.is-drop-target').forEach((s) => s.classList.remove('is-drop-target'));
  }

  function end() {
    _dragged = null;
    clearTargets();
    _grid?.querySelectorAll('.live-slot').forEach((s) => {
      s.classList.remove('is-dragging');
      s.draggable = false;
    });
  }

  // Only a press on the grip makes its panel draggable; releasing without a drag undoes it.
  function onPointerDown(e) {
    const slot = slotOf(e.target instanceof Element ? e.target.closest('[data-grip]') : null);
    if (slot) slot.draggable = true;
  }

  function onPointerUp() {
    if (!_dragged) end();
  }

  function onDragStart(e) {
    const slot = slotOf(e.target);
    if (!slot || !slot.draggable) return;
    _dragged = slot.dataset.panel;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', _dragged);
    slot.classList.add('is-dragging');
  }

  function onDragOver(e) {
    const slot = slotOf(e.target);
    if (!_dragged || !slot) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (slot.classList.contains('is-drop-target') || slot.dataset.panel === _dragged) return;
    clearTargets();
    slot.classList.add('is-drop-target');
  }

  function onDrop(e) {
    const slot = slotOf(e.target);
    if (!_dragged || !slot) return;
    e.preventDefault();
    const id = _dragged;
    const target = slot.dataset.panel;
    end();
    if (id !== target) _ref?.invokeMethodAsync('MovePanel', id, target).catch(() => { });
  }

  const handlers = { pointerdown: onPointerDown, pointerup: onPointerUp, dragstart: onDragStart, dragover: onDragOver, drop: onDrop, dragend: end };

  function attach(grid, dotnetRef) {
    detach();
    if (!grid) return;
    _grid = grid;
    _ref = dotnetRef;
    for (const [name, fn] of Object.entries(handlers)) grid.addEventListener(name, fn);
  }

  function detach() {
    if (_grid) for (const [name, fn] of Object.entries(handlers)) _grid.removeEventListener(name, fn);
    _grid = null;
    _ref = null;
    _dragged = null;
  }

  window.powatchLayout = { attach, detach };
})();

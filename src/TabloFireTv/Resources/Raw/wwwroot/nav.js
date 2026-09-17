// D-pad / keyboard navigation.
//
// This replaces the Fire TV app's synthesized mouse — a drawn cursor sprite, fake MotionEvent
// touch-drags, and a JS→native bridge reporting the scrollable element's on-screen bounds so
// native could decide when the cursor was close enough to an edge to drag. That design had four
// moving parts that all had to agree, and any one of them disagreeing showed up as "scrolling is
// broken": a CSS-px/device-px unit mismatch, a cursor trapped inside the guide grid, an injected
// reporter that went missing and failed closed, a scroll step a fifteenth of a card tall, and
// finally an asymmetry where scrolling one way pinned the cursor at that edge so reversing cost
// ~27 dead button presses before anything moved.
//
// None of that exists here. Arrow keys move DOM focus between real elements, and the only thing
// that scrolls is the browser, via scrollIntoView. There are no coordinates to convert, no bounds
// to report, no gesture to synthesize, and no state that can go stale — which is the actual point:
// the failure mode that kept coming back is structurally absent rather than patched.
//
// It also means the whole thing is testable in a desktop browser with the arrow keys, instead of
// only on a Fire Stick that falls asleep mid-test.
(function () {
  'use strict';

  // Everything the D-pad can land on. `.card` is a plain div, so it gets a tabindex assigned
  // lazily below; the rest are natively focusable already.
  const FOCUSABLE = [
    '.tab',
    '.card',
    '.airing',
    'button:not([disabled])',
    'select',
    'input:not([type=hidden])',
    'a[href]',
    '[data-nav]',
  ].join(', ');

  // Elements that are focusable without help from us.
  const NATIVE = /^(BUTTON|SELECT|INPUT|A|TEXTAREA)$/;

  // How far past the focused element's trailing edge a candidate may start and still count as
  // "ahead". Cards in the same row overlap by their full height, so they never qualify; this only
  // forgives a pixel or two of rounding between siblings meant to be flush.
  const AHEAD_TOLERANCE = 4;

  // Weight on cross-axis distance. Candidates that line up with the focused element (any overlap
  // at all) score 0 here, so travel stays in a straight line down a column or along a row; the
  // weight only decides between off-axis options once nothing is straight ahead.
  const CROSS_WEIGHT = 4;

  // A held Center on the remote arrives as auto-repeating keydowns. Past this, treat it as a
  // long press and fire contextmenu instead of click — that is how Recordings' delete/move sheet
  // is reached, and it used to depend on holding a synthetic touch down natively.
  const LONG_PRESS_MS = 450;

  const $ = (id) => document.getElementById(id);

  // ------------------------------------------------------------------- candidate collection

  // Navigation is confined to the topmost open layer. When the detail sheet is up it is the only
  // thing worth pointing at, so arrows stay inside it and cannot wander onto the view behind —
  // which is what used to make Record unreachable from the Guide, and needed a special-case
  // "warp the cursor onto the button" hack to paper over.
  function layer() {
    const sheet = $('sheet');
    if (sheet && !sheet.hidden) return sheet;
    const player = $('player');
    if (player && !player.hidden) return player;
    return document.body;
  }

  function visible(node) {
    if (node.disabled || node.hidden) return false;
    if (node.closest('[hidden]')) return false;
    const r = node.getBoundingClientRect();
    // Zero-sized means display:none, an unrendered row, or a collapsed guide block.
    return r.width >= 4 && r.height >= 4;
  }

  function candidates() {
    const out = [];
    for (const node of layer().querySelectorAll(FOCUSABLE)) {
      if (!visible(node)) continue;
      if (!NATIVE.test(node.tagName) && !node.hasAttribute('tabindex')) node.tabIndex = 0;
      out.push({ node, rect: node.getBoundingClientRect() });
    }
    return out;
  }

  // ------------------------------------------------------------------------------- geometry

  const overlaps = (aStart, aEnd, bStart, bEnd) =>
    Math.min(aEnd, bEnd) - Math.max(aStart, bStart) > 0;

  const gap = (aStart, aEnd, bStart, bEnd) =>
    bStart > aEnd ? bStart - aEnd : aStart - bEnd;

  // Distance from `from` to `rect` along the travel axis: how far ahead the candidate begins.
  // Negative means it starts behind the focused element's trailing edge, i.e. not in that
  // direction at all.
  function ahead(dir, from, rect) {
    if (dir === 'down') return rect.top - from.bottom;
    if (dir === 'up') return from.top - rect.bottom;
    if (dir === 'right') return rect.left - from.right;
    return from.left - rect.right;
  }

  function crossDistance(dir, from, rect) {
    const vertical = dir === 'up' || dir === 'down';
    const aligned = vertical
      ? overlaps(from.left, from.right, rect.left, rect.right)
      : overlaps(from.top, from.bottom, rect.top, rect.bottom);
    if (aligned) return 0;
    return vertical
      ? gap(from.left, from.right, rect.left, rect.right)
      : gap(from.top, from.bottom, rect.top, rect.bottom);
  }

  // Anything straight ahead wins over anything off to the side, however near. Without that rule a
  // close diagonal could outscore a distant straight line: Left from "Sign out" at the far right of
  // the header dropped onto the guide's date picker below, instead of travelling along the header
  // to the tabs.
  function pick(dir, from, list, current) {
    let best = null;
    let bestScore = Infinity;
    let bestAligned = false;
    for (const { node, rect } of list) {
      if (node === current) continue;
      const forward = ahead(dir, from, rect);
      if (forward < -AHEAD_TOLERANCE) continue;
      const cross = crossDistance(dir, from, rect);
      const aligned = cross === 0;
      if (bestAligned && !aligned) continue;
      const score = Math.max(forward, 0) + cross * CROSS_WEIGHT;
      if ((aligned && !bestAligned) || score < bestScore) {
        bestScore = score;
        best = node;
        bestAligned = aligned;
      }
    }
    return best;
  }

  // ----------------------------------------------------------------------------- moving focus

  function focus(node) {
    if (!node) return;
    // Scroll deliberately rather than letting focus() do it, so the options are ours: 'nearest'
    // moves the least that brings the element fully into view, in every scrollport that contains
    // it — the page for Live TV and Recordings, #guideWrap for the guide grid — without either
    // one needing to know the other exists. The room left around it comes from scroll-padding in
    // style.css, which is also what keeps focus clear of the sticky header and guide ruler.
    node.focus({ preventScroll: true });
    node.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  // Top-left-most candidate, preferring the content of the visible view over the header that sits
  // above every view — arriving on Guide should land in the grid, not back on the tab you just
  // pressed. Falls back to the whole layer when a view has nothing focusable of its own.
  function firstIn(list) {
    const view = document.querySelector('.view:not([hidden])');
    const inView = view ? list.filter((c) => view.contains(c.node)) : [];
    let best = null;
    let bestScore = Infinity;
    for (const { node, rect } of (inView.length ? inView : list)) {
      const score = rect.top * 2 + rect.left;
      if (score < bestScore) {
        bestScore = score;
        best = node;
      }
    }
    return best;
  }

  // Where focus was in each layer, by position in the candidate list. Live TV and Recordings
  // re-render on their own schedule (the "On now" data refreshes, a recording finishes), which
  // detaches the focused card and drops focus to <body>. Without this, every refresh threw the
  // user back to the top of the list — reproduced on-device as presses that appeared to go
  // missing, when what actually happened was the list silently rewinding underneath them.
  //
  // Keyed by layer rather than held as one value so that leaving a view and coming back returns
  // to where you were, instead of the top.
  const positions = new Map();

  function layerKey() {
    const root = layer();
    if (root.id) return root.id;
    const view = document.querySelector('.view:not([hidden])');
    return view ? view.id : 'body';
  }

  function remember(node, list) {
    const index = list.findIndex((c) => c.node === node);
    if (index >= 0) positions.set(layerKey(), index);
  }

  // Puts focus back after it has been lost, as close to where it was as the new list allows.
  function restore(list) {
    const index = positions.get(layerKey());
    const node = index === undefined ? firstIn(list) : list[Math.min(index, list.length - 1)].node;
    focus(node);
    remember(node, list);
  }

  function move(dir) {
    const list = candidates();
    if (!list.length) return;

    const active = document.activeElement;
    if (!active || !list.some((c) => c.node === active)) {
      restore(list);
      return;
    }

    const next = pick(dir, active.getBoundingClientRect(), list, active);
    if (!next) return;
    focus(next);
    remember(next, list);
  }

  // -------------------------------------------------------------------------- key handling

  const DIRECTIONS = {
    ArrowUp: 'up',
    ArrowDown: 'down',
    ArrowLeft: 'left',
    ArrowRight: 'right',
  };

  // A text field owns left/right for its caret, but nothing owns up/down, so those still
  // navigate away — otherwise the search boxes are roach motels on a remote with no Tab key.
  function ownsHorizontal(node) {
    return node && node.tagName === 'INPUT' && node.type !== 'checkbox' && node.type !== 'radio';
  }

  let longPressTimer = null;
  let longPressFired = false;

  // Timed from the real key-down rather than counted off auto-repeat events. Repeats looked like
  // the obvious signal, but they are not reliable here: this remote does not always send them,
  // the native side pumps its own on a cadence of its choosing, and `adb input keyevent
  // --longpress` fires one instantly — so a genuine hold and a tap were indistinguishable, and a
  // hold on a recording played it instead of opening the delete/move sheet. A timer needs nothing
  // but the key going down and coming back up.
  function beginLongPress(target) {
    longPressFired = false;
    clearTimeout(longPressTimer);
    longPressTimer = setTimeout(() => {
      longPressFired = true;
      if (target) {
        target.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
      }
    }, LONG_PRESS_MS);
  }

  document.addEventListener('keydown', (event) => {
    if (event.altKey || event.ctrlKey || event.metaKey) return;

    const dir = DIRECTIONS[event.key];
    if (dir) {
      if ((dir === 'left' || dir === 'right') && ownsHorizontal(document.activeElement)) return;
      event.preventDefault();
      move(dir);
      return;
    }

    if (event.key === 'Enter') {
      // A <select> opens its own picker on Enter, and the platform handles the list from there.
      if (document.activeElement && document.activeElement.tagName === 'SELECT') return;
      event.preventDefault();
      // Auto-repeats while the button is held are noise here — the timer already has this.
      if (!event.repeat) beginLongPress(document.activeElement);
    }
  });

  document.addEventListener('keyup', (event) => {
    if (event.key !== 'Enter') return;
    if (document.activeElement && document.activeElement.tagName === 'SELECT') return;
    event.preventDefault();
    clearTimeout(longPressTimer);
    // The long press already did its thing while the button was down; releasing must not also
    // click, or every delete-sheet gesture would open the sheet and then play the recording
    // behind it.
    if (longPressFired) {
      longPressFired = false;
      return;
    }
    const target = document.activeElement;
    if (target && target !== document.body) target.click();
  });

  // Some remotes have a dedicated menu button; treat it as the long press.
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'ContextMenu') return;
    event.preventDefault();
    const target = document.activeElement;
    if (target && target !== document.body) {
      target.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }));
    }
  });

  // ------------------------------------------------------------------ following the app around

  // app.js switches views and opens/closes the sheet and player purely by toggling `hidden`, and
  // it does not need to know this file exists. Watching that one attribute is enough to put focus
  // somewhere sensible whenever the visible layer changes.
  let settle = null;
  let currentKey = null;
  function onLayerChanged() {
    clearTimeout(settle);
    // Coalesce: switching a view flips `hidden` on the outgoing and incoming sections in the same
    // tick, and a guide render replaces hundreds of nodes at once.
    settle = setTimeout(() => {
      const list = candidates();
      if (!list.length) return;
      const key = layerKey();
      const active = document.activeElement;
      const held = active && active !== document.body && list.some((c) => c.node === active);

      // A change of layer always re-homes, even when focus is technically still valid. The tab
      // bar belongs to every view, so after pressing Guide focus is sitting on a live candidate
      // and nothing looks wrong — but leaving it there strands the user in the header of a view
      // they just navigated to, and abandons the position they had in it.
      if (key !== currentKey) {
        currentKey = key;
        restore(list);
        return;
      }

      // Same layer: this is a re-render. Only step in if it took focus with it.
      if (!held) restore(list);
    }, 60);
  }

  new MutationObserver(onLayerChanged).observe(document.body, {
    subtree: true,
    childList: true,
    attributes: true,
    attributeFilter: ['hidden'],
  });

  // Give focus somewhere to start once the first view has rendered.
  document.addEventListener('DOMContentLoaded', onLayerChanged);
  onLayerChanged();
})();

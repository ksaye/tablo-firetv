/* Tablo web — the whole front end. Plain modules-free JavaScript on purpose: this is served
   off a small box on the LAN and there is no build step to go wrong. */

'use strict';

const PX_PER_MIN = 5;        // guide horizontal scale
const GUIDE_HOURS = 4;       // how much of the schedule is on screen at once
const $ = (id) => document.getElementById(id);

const state = {
  view: 'live',
  status: null,
  channels: [],
  channelByPath: new Map(),
  now: [],
  recordings: [],
  guide: { start: null, airings: [], loaded: false },

  // What the player has open, and which transcode is currently serving it. These are separate
  // because a seek tears the session down and builds a new one, and during those few seconds
  // the program is still open — the scrubber has to keep working.
  media: null,          // { path, live, duration }
  play: null,           // { sessionId, offset }
  pendingPosition: 0    // where a start/restart is heading, until it lands
};

// ---------------------------------------------------------------------------- helpers

async function api(path, options) {
  const res = await fetch(path, options);
  // A 30-day session still expires eventually, usually on a tab that has been open for weeks.
  // Send the visitor back to the sign-in rather than showing them a parse error.
  if (res.status === 401) {
    location.href = '/login?next=' + encodeURIComponent(location.pathname + location.search);
    throw new Error('Your session has expired. Signing in again…');
  }
  if (!res.ok && res.status !== 202) {
    let message = res.statusText;
    try { message = (await res.json()).error || message; } catch { /* not json */ }
    throw new Error(message);
  }
  return { status: res.status, body: await res.json() };
}

const pad = (n) => String(n).padStart(2, '0');

function fmtClock(date) {
  let h = date.getHours(), m = date.getMinutes();
  const ampm = h >= 12 ? 'PM' : 'AM';
  h = h % 12 || 12;
  return `${h}:${pad(m)} ${ampm}`;
}

function fmtDay(date) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const d = new Date(date); d.setHours(0, 0, 0, 0);
  const days = Math.round((d - today) / 86400000);
  if (days === 0) return 'Today';
  if (days === 1) return 'Tomorrow';
  if (days === -1) return 'Yesterday';
  return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
}

function fmtDuration(seconds) {
  seconds = Math.max(0, Math.round(seconds));
  const h = Math.floor(seconds / 3600), m = Math.round((seconds % 3600) / 60);
  return h ? `${h}h ${m}m` : `${m}m`;
}

function fmtPosition(seconds) {
  seconds = Math.max(0, Math.floor(seconds));
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60), s = seconds % 60;
  return h ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
}

function fmtBytes(bytes) {
  if (!bytes) return '—';
  const gb = bytes / 1073741824;
  return gb >= 1000 ? `${(gb / 1024).toFixed(1)} TB` : `${gb.toFixed(1)} GB`;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

function showBanner(message) {
  const banner = $('banner');
  if (!message) { banner.hidden = true; return; }
  banner.textContent = message;
  banner.hidden = false;
}

// ------------------------------------------------------------------------ navigation

function setView(view) {
  state.view = view;
  for (const tab of document.querySelectorAll('.tab')) {
    tab.setAttribute('aria-selected', String(tab.dataset.view === view));
  }
  for (const section of document.querySelectorAll('.view')) {
    section.hidden = section.id !== `view-${view}`;
  }
  location.hash = view;
  if (view === 'live') loadNow();
  if (view === 'multiview') loadMultiview();
  if (view === 'recordings') loadRecordings();
  if (view === 'guide') loadGuide();
}

document.querySelectorAll('.tab').forEach((tab) =>
  tab.addEventListener('click', () => setView(tab.dataset.view)));

// ---------------------------------------------------------------------------- status

async function pollStatus() {
  try {
    const { body } = await api('/api/status');
    state.status = body;

    // No Tablo account on the server at all — a fresh install, or someone signed out and forgot
    // the credentials. There is nothing to show until that is fixed.
    if (body.needsSetup) { location.href = '/login'; return; }

    const dot = $('statusDot');
    dot.className = 'dot ' + (body.connected ? (body.guideReady ? 'ok' : 'busy') : 'bad');

    if (body.connected) {
      const parts = [body.model || 'Tablo'];
      if (body.storageTotalBytes) parts.push(`${fmtBytes(body.storageFreeBytes)} free`);
      if (body.activeStreams) parts.push(`${body.activeStreams} streaming`);
      $('statusText').textContent = parts.join(' · ');
      $('status').title = `${body.deviceName || 'Tablo'} at ${body.deviceHost} · firmware ${body.firmware} · ${body.tuners} tuners`;
      showBanner(null);
    } else {
      $('statusText').textContent = 'Not connected';
      showBanner(body.state);
    }

    // The guide view shows its own progress while the first load runs.
    if (!body.guideReady && state.view === 'guide') {
      $('guideBar').style.width = `${Math.round((body.guideProgress || 0) * 100)}%`;
    }
  } catch (err) {
    $('statusDot').className = 'dot bad';
    $('statusText').textContent = 'Server unreachable';
  }
}

// ------------------------------------------------------------------------- live view

async function loadChannels() {
  if (state.channels.length) return state.channels;
  const { body } = await api('/api/channels');
  state.channels = body;
  state.channelByPath = new Map(body.map((c) => [c.path, c]));
  return body;
}

async function loadNow() {
  try {
    await loadChannels();
    const { body } = await api('/api/now');
    state.now = body;
    renderNow();
  } catch (err) {
    $('liveList').replaceChildren(el('div', 'empty', `Could not load channels: ${err.message}`));
  }
}

function renderNow() {
  const filter = $('liveSearch').value.trim().toLowerCase();
  const list = $('liveList');
  const matches = state.now.filter(({ channel, airing }) =>
    !filter ||
    `${channel.number} ${channel.callSign} ${channel.name} ${airing ? airing.title : ''}`
      .toLowerCase().includes(filter));

  if (!matches.length) {
    list.replaceChildren(el('div', 'empty',
      state.now.length ? 'No channels match that filter.' : 'No channels found. Has the Tablo been scanned?'));
    return;
  }

  list.replaceChildren(...matches.map(({ channel, airing, progress }) => {
    const card = el('div', 'card live-card');
    const body = el('div', 'card-body');

    const head = el('div', 'live-num', `${channel.number}  ${channel.callSign}`);
    // Streaming channels come over the internet, not the antenna — worth saying, because
    // they are the ones that cannot be recorded.
    if (channel.isFast) head.append(' ', el('span', 'badge free', 'FREE'));
    body.append(head);
    body.append(el('div', 'card-title', airing ? airing.title : channel.name));
    if (airing && airing.subtitle) body.append(el('div', 'card-sub', airing.subtitle));

    if (airing) {
      const start = new Date(airing.start);
      const end = new Date(start.getTime() + airing.durationSeconds * 1000);
      body.append(el('div', 'card-meta', `${fmtClock(start)} – ${fmtClock(end)}`));
      const bar = el('div', 'live-prog');
      const fill = el('i');
      fill.style.width = `${Math.round(progress * 100)}%`;
      bar.append(fill);
      body.append(bar);
    } else {
      body.append(el('div', 'card-meta', 'No guide data for this channel'));
    }

    card.append(body);
    card.addEventListener('click', () =>
      playLive(channel, airing));
    return card;
  }));
}

$('liveSearch').addEventListener('input', renderNow);

// -------------------------------------------------------------------- multi-view

/* Several live channels tiled into one stream by the server (see MosaicManager). The picker
   below chooses 2–4; the server composites them and hands back a single HLS URL whose audio
   renditions are the individual panes, so switching the sound between panes is a track change,
   not a re-encode.

   Nothing in here may touch `video` at load time — `const video` is not initialised until the
   player section further down, and a top-level reference here would throw before the rest of
   this file ever ran. The mute listeners therefore live down there, next to the others. */

const mv = {
  selected: [],          // channel paths, in pick order
  session: null,         // { id, url, channels }
  audioPane: 0,
  rotateTimer: null,
  muted: false           // a mosaic starts muted for mobile autoplay; the first gesture lifts it
};

async function loadMultiview() {
  try {
    await loadChannels();
    const { body } = await api('/api/now');
    state.now = body;
  } catch { /* renderMultiview copes with an empty list */ }
  renderMultiview();
  // If a mosaic is already running — a page reload, or another tab — rejoin it rather than
  // leaving an orphan holding every tuner.
  reattachMosaic();
}

function renderMultiview() {
  const list = $('mvList');
  // Antenna and free streaming channels alike; multi-view has no concept of a recording.
  const rows = state.now;

  if (!rows.length) {
    list.replaceChildren(el('div', 'empty', 'No channels found.'));
    return;
  }

  list.replaceChildren(...rows.map(({ channel, airing }) => {
    const card = el('div', 'card live-card mv-card');
    card.dataset.path = channel.path;

    const body = el('div', 'card-body');
    const head = el('div', 'live-num', `${channel.number}  ${channel.callSign}`);
    if (channel.isFast) head.append(' ', el('span', 'badge free', 'FREE'));
    body.append(head);
    body.append(el('div', 'card-title', airing ? airing.title : channel.name));
    if (airing && airing.subtitle) body.append(el('div', 'card-sub', airing.subtitle));
    card.append(body);
    card.addEventListener('click', () => toggleMvPick(channel.path));
    return card;
  }));

  refreshMvSelection();
}

/* Apply the current selection to the cards already on screen — WITHOUT rebuilding the list.
   A full re-render detaches the card that was just tapped, which drops focus to <body> and, on a
   touch device, scrolls the list back to the top on every pick. */
function refreshMvSelection() {
  for (const card of $('mvList').querySelectorAll('.mv-card')) {
    const pos = mv.selected.indexOf(card.dataset.path);
    card.classList.toggle('selected', pos >= 0);
    if (pos >= 0) card.dataset.pick = pos + 1; else delete card.dataset.pick;
  }
  const n = mv.selected.length;
  $('mvCount').textContent = n ? `${n} selected` : '';
  $('mvStart').disabled = n < 2 || n > 4;
  $('mvStart').textContent = mv.session ? 'Restart multi-view' : 'Start multi-view';
}

function toggleMvPick(path) {
  const i = mv.selected.indexOf(path);
  if (i >= 0) mv.selected.splice(i, 1);
  else if (mv.selected.length < 4) mv.selected.push(path);
  else { showBanner('Multi-view takes at most 4 channels.'); setTimeout(() => showBanner(null), 3000); }
  refreshMvSelection();
}

$('mvStart').addEventListener('click', () => startMosaic(mv.selected));

async function startMosaic(channels) {
  if (channels.length < 2) return;
  openPlayer('Multi-view', channels.length + ' channels');
  $('scrub').hidden = true;
  setOverlay('Tuning the channels and starting the mosaic…');
  try {
    const { body } = await api('/api/mosaic', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ channels })
    });
    installMosaic(body);
  } catch (err) {
    setOverlay(`Could not start multi-view. ${err.message}`);
  }
}

/* Wire up a running mosaic session in the player: attach the single stream, show the control
   bar, and reset the audio/rotate state. Used by both startMosaic and reattachMosaic. */
function installMosaic(session) {
  mv.session = session;
  mv.audioPane = 0;
  state.media = { mosaic: true, live: true, duration: 0, id: session.id };
  $('playerSub').textContent = (session.channels || []).map(c => c.number).join(' · ');

  // Mobile browsers block autoplay WITH sound — video.play() just rejects and the picture never
  // starts, leaving the tuning overlay up for ever. Muted autoplay is allowed everywhere, so
  // start muted and lift it on the first real interaction (a pane tap, a tap on the picture, an
  // arrow key).
  video.muted = true;
  mv.muted = true;

  // The browser's own controls have to go while a mosaic plays. On a touchscreen their shadow DOM
  // swallows the tap — the first one only shows the control bar — so tapping a pane did nothing
  // at all in full screen, where the picture is the only thing to tap. They offer nothing here
  // anyway: a mosaic is live, so there is no seeking, and the volume is the device's own.
  video.controls = false;

  attach(session.url);
  renderMosaicPanes(session.channels || []);
  $('mosaicBar').hidden = false;
  startRotate();          // the audio auto-cycles by default
  watchMosaic();
  refreshMvSelection();   // just the "Restart multi-view" label — don't rebuild the list
}

function renderMosaicPanes(channels) {
  $('mosaicPanes').replaceChildren(...channels.map((c, i) => {
    const b = el('button', 'ghost mosaic-pane', `${i + 1} ${c.callSign || c.number || ''}`.trim());
    b.setAttribute('aria-pressed', String(i === mv.audioPane));
    b.addEventListener('click', () => { stopRotate(); setAudioPane(i); });
    return b;
  }));
}

// Lift the mute a mosaic starts with (see installMosaic). Called from the first real gesture:
// a pane button, a tap on the picture, or an arrow key — all of which count as user activation.
function mvUnmute() {
  if (!mv.muted) return;
  mv.muted = false;
  video.muted = false;
  video.play().catch(() => { /* already playing */ });
  if (state.media && state.media.mosaic) setOverlay(null);
}

// Point the sound at pane i — an instant audio-track switch, because the composite carries every
// pane's audio as a rendition and nothing on the server has to change.
function setAudioPane(i) {
  mvUnmute();
  const tracks = hls && hls.audioTracks ? hls.audioTracks.length : 0;
  if (tracks && i >= 0 && i < tracks) hls.audioTrack = i;
  mv.audioPane = i;
  for (const [k, b] of [...$('mosaicPanes').children].entries()) {
    b.setAttribute('aria-pressed', String(k === i));
  }
  // Move the yellow box on the composite to this pane. Fire-and-forget: the box lagging the
  // sound by a fraction of a second, or not moving at all, is harmless.
  if (mv.session) {
    fetch(`/api/mosaic/${mv.session.id}/audio`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pane: i })
    }).catch(() => { /* best effort */ });
  }
}

function cycleAudioPane(delta) {
  const count = (mv.session && mv.session.channels || []).length || 1;
  setAudioPane((mv.audioPane + delta + count) % count);
}

// --- audio auto-cycle (on by default) ---
function startRotate() {
  stopRotate();
  const secs = parseInt($('mosaicRotateSecs').value, 10) || 45;
  mv.rotateTimer = setInterval(() => cycleAudioPane(1), secs * 1000);
  $('mosaicRotateBtn').setAttribute('aria-pressed', 'true');
}
function stopRotate() {
  clearInterval(mv.rotateTimer);
  mv.rotateTimer = null;
  $('mosaicRotateBtn').setAttribute('aria-pressed', 'false');
}
function toggleRotate() { mv.rotateTimer ? stopRotate() : startRotate(); }

$('mosaicRotateBtn').addEventListener('click', toggleRotate);
$('mosaicRotateSecs').addEventListener('change', () => { if (mv.rotateTimer) startRotate(); });

/* Poll so a mosaic transcoder that dies is reported rather than looking like a frozen picture.
   No keepalive is sent — a mosaic holds tuners and is left to the server's idle reaper, exactly
   like a plain live stream. */
let mosaicWatch = null;
function watchMosaic() {
  clearInterval(mosaicWatch);
  mosaicWatch = setInterval(async () => {
    if (!mv.session) return;
    try {
      const { body } = await api(`/api/mosaic/${mv.session.id}`);
      if (body.finished) setOverlay(`Multi-view stopped. ${body.lastError || ''}`);
    } catch (err) {
      if (err.status === 404) { setOverlay('Multi-view has ended.'); teardownMosaic(false); }
    }
  }, 10000);
}

async function reattachMosaic() {
  try {
    const { body } = await api('/api/mosaic');
    if (body.running && (!mv.session || mv.session.id !== body.id)) {
      openPlayer('Multi-view', (body.channels || []).length + ' channels');
      $('scrub').hidden = true;
      installMosaic(body);
    }
  } catch { /* nothing running, or the endpoint is unreachable */ }
}

function teardownMosaic(stopServer) {
  clearInterval(mosaicWatch);
  stopRotate();
  video.muted = false;
  mv.muted = false;
  video.controls = true;          // back for ordinary playback (see installMosaic)
  dockMosaicBar(false);
  $('mosaicBar').hidden = true;
  const session = mv.session;
  mv.session = null;
  if (stopServer && session) {
    fetch(`/api/mosaic/${session.id}/stop`, { method: 'POST' }).catch(() => { /* best effort */ });
  }
  refreshMvSelection();
}

/* The arrows drive the sound while a mosaic is playing. Left/right step it through the panes and
   pin it there; up/down hand it back to the auto-cycle — and step once straight away as well as
   re-arming the timer, because without that immediate step the press has no visible or audible
   effect for a whole interval and reads as a dead key. In full screen there is no chrome on
   screen, so the pane change is the only feedback there is.

   Capture phase, and no "are they typing" guard: with the mosaic player up there is nothing on
   screen to type into, and the player's own space/F handler further down must not eat these. */
document.addEventListener('keydown', (e) => {
  if (!state.media || !state.media.mosaic || $('player').hidden) return;
  const k = e.key;
  if (k === 'ArrowLeft') {
    e.preventDefault(); mvUnmute(); stopRotate(); cycleAudioPane(-1);
  } else if (k === 'ArrowRight') {
    e.preventDefault(); mvUnmute(); stopRotate(); cycleAudioPane(1);
  } else if (k === 'ArrowUp' || k === 'ArrowDown') {
    e.preventDefault(); mvUnmute(); startRotate(); cycleAudioPane(1);
  } else if (k === 'Enter' || k === ' ') {
    mvUnmute();
  }
}, true);

// ------------------------------------------------------------------- recordings view

async function loadRecordings(force) {
  if (state.recordings.length && !force) { renderRecordings(); return; }
  $('recList').replaceChildren(el('div', 'empty', 'Loading recordings…'));
  try {
    const { body } = await api('/api/recordings' + (force ? '?refresh=true' : ''));
    state.recordings = body;
    renderRecordings();
  } catch (err) {
    $('recList').replaceChildren(el('div', 'empty', `Could not load recordings: ${err.message}`));
  }
}

function renderRecordings() {
  const filter = $('recSearch').value.trim().toLowerCase();
  const sort = $('recSort').value;

  let items = state.recordings.filter((r) =>
    !filter || `${r.title} ${r.subtitle || ''} ${r.description}`.toLowerCase().includes(filter));

  if (sort === 'title') items = [...items].sort((a, b) => a.title.localeCompare(b.title));
  else if (sort === 'size') items = [...items].sort((a, b) => b.sizeBytes - a.sizeBytes);

  $('recCount').textContent = state.recordings.length
    ? `${items.length}${items.length !== state.recordings.length ? ` of ${state.recordings.length}` : ''}`
    : '';

  if (!items.length) {
    $('recList').replaceChildren(el('div', 'empty',
      state.recordings.length ? 'Nothing matches that search.' : 'Nothing has been recorded yet.'));
    return;
  }

  $('recList').replaceChildren(...items.map((rec) => {
    const card = el('div', 'card');

    const thumb = el('div', 'thumb');
    if (rec.imageId) thumb.style.backgroundImage = `url(/api/image/${rec.imageId})`;
    else thumb.textContent = '▶';

    // Where they stopped watching, so a part-watched recording says so at a glance.
    if (rec.resumeSeconds > 30 && rec.durationSeconds) {
      const bar = el('div', 'progress');
      const fill = el('i');
      fill.style.width = `${Math.min(100, (rec.resumeSeconds / rec.durationSeconds) * 100)}%`;
      bar.append(fill);
      thumb.append(bar);
    }
    card.append(thumb);

    const body = el('div', 'card-body');
    body.append(el('div', 'card-title', rec.title));
    if (rec.subtitle) body.append(el('div', 'card-sub', rec.subtitle));

    const meta = el('div', 'card-meta');
    if (rec.channelNumber) {
      meta.append(el('span', 'chan', `${rec.channelNumber} ${rec.channelCallSign || ''}`.trim()));
    }
    if (rec.start) {
      const when = new Date(rec.start);
      meta.append(el('span', null, `${fmtDay(when)} ${fmtClock(when)}`));
    }
    meta.append(el('span', null, fmtDuration(rec.durationSeconds)));
    if (rec.sizeBytes) meta.append(el('span', null, fmtBytes(rec.sizeBytes)));
    if (rec.state === 'recording') meta.append(el('span', 'badge rec', 'RECORDING'));
    if (rec.protected) meta.append(el('span', 'badge kept', 'KEPT'));
    if (rec.watched) meta.append(el('span', 'badge watched', 'WATCHED'));
    body.append(meta);

    card.append(body);
    card.addEventListener('click', () => playRecording(rec));
    return card;
  }));
}

$('recSearch').addEventListener('input', renderRecordings);
$('recSort').addEventListener('change', renderRecordings);

// ------------------------------------------------------------------------ guide view

function buildDayPicker() {
  const select = $('guideDay');
  if (select.options.length) return;
  const today = new Date(); today.setHours(0, 0, 0, 0);
  for (let i = 0; i < 14; i++) {
    const day = new Date(today.getTime() + i * 86400000);
    select.append(new Option(fmtDay(day), day.toISOString()));
  }
}

function defaultStartFor(day) {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  const start = new Date(day);
  if (start.getTime() === today.getTime()) {
    // Today opens where the viewer is: the current half hour.
    const now = new Date();
    now.setMinutes(now.getMinutes() < 30 ? 0 : 30, 0, 0);
    return now;
  }
  start.setHours(19, 0, 0, 0);   // any other day opens at prime time
  return start;
}

async function loadGuide(start) {
  buildDayPicker();
  if (!start) start = state.guide.start || defaultStartFor(new Date($('guideDay').value));
  state.guide.start = start;

  // Keep the day selector honest when the arrows walk past midnight.
  const dayValue = new Date(start); dayValue.setHours(0, 0, 0, 0);
  const option = [...$('guideDay').options].find((o) => new Date(o.value).getTime() === dayValue.getTime());
  if (option) $('guideDay').value = option.value;

  try {
    const { status, body } = await api(
      `/api/guide?start=${encodeURIComponent(start.toISOString())}&hours=${GUIDE_HOURS}`);

    if (status === 202 || body.loading) {
      $('guideLoading').hidden = false;
      $('guideWrap').hidden = true;
      $('guideBar').style.width = `${Math.round((body.progress || 0) * 100)}%`;
      clearTimeout(loadGuide.retry);
      loadGuide.retry = setTimeout(() => { if (state.view === 'guide') loadGuide(start); }, 3000);
      return;
    }

    $('guideLoading').hidden = true;
    $('guideWrap').hidden = false;
    state.channels = body.channels;
    state.channelByPath = new Map(body.channels.map((c) => [c.path, c]));
    state.guide.airings = body.airings;
    state.guide.loaded = true;
    renderGuide(start);
  } catch (err) {
    $('guideLoading').hidden = true;
    $('guideWrap').hidden = false;
    $('guideBody').replaceChildren(el('div', 'empty', `Could not load the guide: ${err.message}`));
  }
}

function renderGuide(start) {
  const windowStart = start.getTime();
  const windowEnd = windowStart + GUIDE_HOURS * 3600000;
  const width = GUIDE_HOURS * 60 * PX_PER_MIN;

  // Half-hour ruler across the top.
  const times = el('div', 'slots');
  times.style.width = `${width}px`;
  for (let m = 0; m < GUIDE_HOURS * 60; m += 30) {
    const slot = el('div', 'slot', fmtClock(new Date(windowStart + m * 60000)));
    slot.style.width = `${30 * PX_PER_MIN}px`;
    times.append(slot);
  }
  $('guideTimes').replaceChildren(el('div', 'spacer'), times);

  const byChannel = new Map();
  for (const airing of state.guide.airings) {
    if (!byChannel.has(airing.channelPath)) byChannel.set(airing.channelPath, []);
    byChannel.get(airing.channelPath).push(airing);
  }

  const now = Date.now();
  const rows = state.channels.map((channel) => {
    const row = el('div', 'guide-row');

    const head = el('div', 'guide-chan');
    head.append(el('b', null, channel.number), el('span', null, channel.callSign));
    if (channel.isFast) head.append(el('span', 'badge free', 'FREE'));
    row.append(head);

    const slots = el('div', 'guide-slots');
    slots.style.width = `${width}px`;

    for (let m = 30; m < GUIDE_HOURS * 60; m += 30) {
      const line = el('div', 'gridline');
      line.style.left = `${m * PX_PER_MIN}px`;
      slots.append(line);
    }

    for (const airing of byChannel.get(channel.path) || []) {
      const airStart = new Date(airing.start).getTime();
      const airEnd = airStart + airing.durationSeconds * 1000;
      const left = Math.max(0, (airStart - windowStart) / 60000) * PX_PER_MIN;
      const right = Math.min(width, ((Math.min(airEnd, windowEnd) - windowStart) / 60000) * PX_PER_MIN);
      if (right - left < 2) continue;

      const block = el('button', 'airing');
      if (airing.scheduleState === 'scheduled') block.classList.add('scheduled');
      if (airing.scheduleState === 'conflict') block.classList.add('conflict');
      if (airStart <= now && airEnd > now) block.classList.add('onair');
      block.style.left = `${left}px`;
      block.style.width = `${right - left - 3}px`;
      block.append(el('b', null, airing.title));
      block.append(el('span', null, airing.subtitle || fmtClock(new Date(airStart))));
      if (airing.scheduleState !== 'none') block.append(el('i', 'tag'));
      block.addEventListener('click', () => showAiring(airing, channel));
      slots.append(block);
    }

    // A red line marking the present, but only on the window that actually contains it.
    if (now >= windowStart && now < windowEnd) {
      const line = el('div', 'nowline');
      line.style.left = `${((now - windowStart) / 60000) * PX_PER_MIN}px`;
      slots.append(line);
    }

    row.append(slots);
    return row;
  });

  $('guideBody').replaceChildren(...(rows.length ? rows : [el('div', 'empty', 'No channels.')]));
}

$('guideDay').addEventListener('change', () => loadGuide(defaultStartFor(new Date($('guideDay').value))));
$('guidePrev').addEventListener('click', () => loadGuide(new Date(state.guide.start.getTime() - 7200000)));
$('guideNext').addEventListener('click', () => loadGuide(new Date(state.guide.start.getTime() + 7200000)));
$('guideNow').addEventListener('click', () => {
  const today = new Date(); today.setHours(0, 0, 0, 0);
  $('guideDay').value = today.toISOString();
  loadGuide(defaultStartFor(today));
});

// --------------------------------------------------------------------- detail sheet

function showAiring(airing, channel) {
  const start = new Date(airing.start);
  const end = new Date(start.getTime() + airing.durationSeconds * 1000);

  $('sheetTitle').textContent = airing.title;
  $('sheetSub').textContent = airing.subtitle || '';
  $('sheetDesc').textContent = airing.description || 'No description.';

  const meta = [
    `${channel.number} ${channel.callSign}`,
    `${fmtDay(start)} ${fmtClock(start)} – ${fmtClock(end)}`,
    fmtDuration(airing.durationSeconds)
  ];
  if (airing.isNew) meta.push('New');
  if (airing.scheduleState === 'scheduled') meta.push('Scheduled to record');
  if (airing.scheduleState === 'conflict') meta.push('Recording conflict');
  $('sheetMeta').replaceChildren(...meta.map((m) => el('span', null, m)));

  const actions = [];
  const now = Date.now();
  if (start.getTime() <= now && end.getTime() > now) {
    const watch = el('button', 'primary', 'Watch live');
    watch.addEventListener('click', () => { closeSheet(); playLive(channel, airing); });
    actions.push(watch);
  }
  $('sheetActions').replaceChildren(...actions);
  $('sheet').hidden = false;
}

function closeSheet() { $('sheet').hidden = true; }
$('sheetClose').addEventListener('click', closeSheet);

// -------------------------------------------------------------------------- player

const video = $('video');
let hls = null;
let sessionWatch = null;

function playLive(channel, airing) {
  openPlayer('', '');
  showLiveTitle(channel, airing);
  $('scrub').hidden = true;
  state.media = { path: channel.path, live: true, duration: 0 };
  startSession(channel.path, true, 0, 0);
}

/* A channel gets watched for hours, so the heading has to move on when the programme does —
   otherwise the player spends the evening naming whatever happened to be on at tune-in.
   liveTitleUntil is when what we are showing ends; until then there is nothing to ask about. */
let liveTitleUntil = 0;

function showLiveTitle(channel, airing) {
  $('playerTitle').textContent = airing ? airing.title : `${channel.number} ${channel.callSign}`;
  $('playerSub').textContent = `${channel.number} ${channel.callSign}` +
    (airing && airing.subtitle ? ` · ${airing.subtitle}` : '');
  // With no guide data for the channel there is no boundary to wait for, so settle for
  // looking again occasionally rather than on every tick.
  liveTitleUntil = airing
    ? new Date(airing.start).getTime() + airing.durationSeconds * 1000
    : Date.now() + 300000;
}

/// Re-read what is on, but only once the programme in the heading has actually ended.
/// /api/now is served from the cached guide, so this costs the Tablo nothing.
async function refreshLiveTitle() {
  // A mosaic is several channels at once, so there is no single programme to name.
  if (!state.media || !state.media.live || state.media.mosaic || Date.now() < liveTitleUntil) return;
  try {
    const { body } = await api('/api/now');
    state.now = body;
    const entry = body.find((n) => n.channel.path === state.media.path);
    if (entry) showLiveTitle(entry.channel, entry.airing);
  } catch { /* transient — the next tick tries again */ }
}

function playRecording(rec) {
  const meta = [rec.channelNumber ? `${rec.channelNumber} ${rec.channelCallSign || ''}`.trim() : null,
                rec.start ? `${fmtDay(new Date(rec.start))} ${fmtClock(new Date(rec.start))}` : null,
                rec.subtitle].filter(Boolean).join(' · ');
  openPlayer(rec.title, meta);
  $('scrub').hidden = false;

  // Pick up where they left off, unless they were basically at the end.
  const resume = rec.resumeSeconds > 30 && rec.resumeSeconds < rec.durationSeconds - 60
    ? rec.resumeSeconds : 0;
  state.media = { path: rec.path, live: false, duration: rec.durationSeconds };
  startSession(rec.path, false, resume, rec.durationSeconds);
}

function openPlayer(title, subtitle) {
  $('playerTitle').textContent = title;
  $('playerSub').textContent = subtitle || '';
  $('player').hidden = false;
  $('scrim').hidden = false;
  setOverlay('Asking the Tablo for the stream…');
}

function setOverlay(message, subtle) {
  const overlay = $('playerOverlay');
  clearTimeout(setOverlay.pending);
  if (!message) { overlay.hidden = true; return; }
  $('playerMsg').textContent = message;
  overlay.classList.toggle('subtle', !!subtle);
  overlay.querySelector('.spinner').hidden = message.startsWith('Could not');
  overlay.hidden = false;
}

/* Starting a stream takes a few seconds, and in that window the viewer can pick something else
   or close the player. Each attempt takes a ticket; if the ticket is stale by the time the
   server answers, the session it just created is stopped instead of installed — otherwise an
   abandoned live stream sits on a tuner until the server-side reaper notices. */
let startTicket = 0;

async function startSession(path, live, position, duration) {
  // Picking a programme while multi-view is up means the mosaic has to go: it is holding the
  // tuner this is about to ask for, and the server would refuse rather than take it back.
  if (mv.session) teardownMosaic(true);
  await stopSession();          // bumps the ticket, superseding anything still starting
  const ticket = ++startTicket;

  // Move the scrubber to where we are going right away, so a seek feels immediate even though
  // the transcode behind it takes a few seconds to catch up.
  state.pendingPosition = position;
  renderScrub();

  setOverlay(live
    ? 'Tuning the channel and starting the transcoder…'
    : 'Starting the transcoder — this takes a few seconds…');

  try {
    const { body } = await api('/api/play', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ path, live, position, duration })
    });

    if (ticket !== startTicket) {
      if (body.sessionId) fetch(`/api/stop/${body.sessionId}`, { method: 'POST' }).catch(() => { /* best effort */ });
      return;
    }

    state.play = { sessionId: body.sessionId, offset: body.offsetSeconds };
    attach(body.url);
    // A free streaming channel plays straight from the partner's CDN: no session id, because
    // there is no transcoder and no tuner behind it to watch or shut down.
    if (body.sessionId) watchSession();
  } catch (err) {
    if (ticket === startTicket) setOverlay(`Could not start playback. ${err.message}`);
  }
}

function attach(url) {
  detachPlayer();

  if (window.Hls && Hls.isSupported()) {
    hls = new Hls({
      // The playlist grows as ffmpeg transcodes, so keep hls.js patient about gaps and let it
      // reload the playlist often enough to notice new segments.
      lowLatencyMode: false,
      // The mosaic plays closer to the live edge: the yellow "this pane has the sound" box is
      // baked into the video by the server, so the further behind live we play, the longer it
      // lags the (client-side, instant) audio switch. Two segments back instead of four — and
      // the mosaic's segments are half the length, so the cushion is a quarter of live TV's.
      liveSyncDurationCount: (state.media && state.media.mosaic) ? 2 : 4,
      manifestLoadingMaxRetry: 6,
      levelLoadingMaxRetry: 10,
      fragLoadingMaxRetry: 6
    });
    hls.on(Hls.Events.MANIFEST_PARSED, () => video.play().catch(() => { /* autoplay may be blocked */ }));
    // A manifest may advertise subtitle renditions of its own (the FAST channels do); the
    // embedded 608 track arrives later, through addtrack.
    hls.on(Hls.Events.SUBTITLE_TRACKS_UPDATED, applyCaptions);
    hls.on(Hls.Events.ERROR, (_, data) => {
      if (!data.fatal) return;
      if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
        // A reaped session 404s; anything else is usually worth one more try.
        setOverlay('Reconnecting to the stream…');
        hls.startLoad();
      } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
        hls.recoverMediaError();
      } else {
        setOverlay('Could not play this stream. ' + (data.details || ''));
      }
    });
    hls.loadSource(url);
    hls.attachMedia(video);
  } else {
    // Safari plays HLS itself.
    video.src = url;
    video.play().catch(() => { /* autoplay may be blocked */ });
  }
}

video.addEventListener('playing', () => setOverlay(null));

// Mosaic: the picture plays muted until a gesture (see installMosaic / mvUnmute). Once it is
// running, say the sound is waiting; a tap on the picture is one of the gestures that lifts it.
video.addEventListener('playing', () => {
  if (mv.session && mv.muted) setOverlay('🔇  Tap a channel for sound', true);
});
/* Tapping a pane in the picture moves the sound to it. This is the only control a phone has in
   full screen — the button bar lives outside #playerStage and so is not on screen there, and
   there are no arrow keys to fall back on. It is also what the "tap a channel for sound" hint
   has always promised. */
$('playerStage').addEventListener('click', (event) => {
  if (!mv.session) return;
  mvUnmute();
  const pane = paneAtPoint(event.clientX, event.clientY);
  if (pane >= 0) { stopRotate(); setAudioPane(pane); }
});

/* Which pane is under a point on screen.

   The layout has to be mirrored from the server's (MosaicManager.Rects) because the composite
   arrives as one flat picture — there is nothing in the DOM to hit-test. Fractions of the canvas,
   so they hold at any size. */
function paneLayout(n) {
  if (n === 2) return [[0, 0.25, 0.5, 0.5], [0.5, 0.25, 0.5, 0.5]];
  if (n === 3) return [[0, 0, 0.5, 0.5], [0.5, 0, 0.5, 0.5], [0.25, 0.5, 0.5, 0.5]];
  return [[0, 0, 0.5, 0.5], [0.5, 0, 0.5, 0.5], [0, 0.5, 0.5, 0.5], [0.5, 0.5, 0.5, 0.5]];
}

function paneAtPoint(clientX, clientY) {
  const count = (mv.session && mv.session.channels || []).length;
  if (count < 2) return -1;

  const box = video.getBoundingClientRect();
  if (!box.width || !box.height) return -1;

  // In full screen the video is letterboxed inside the element (object-fit: contain), so the
  // picture is smaller than what was tapped on; windowed it fills the box exactly. Work out the
  // drawn rectangle, and fall back to the element itself for a tap in the black margins.
  let fx = (clientX - box.left) / box.width;
  let fy = (clientY - box.top) / box.height;
  if (video.videoWidth && video.videoHeight) {
    const scale = Math.min(box.width / video.videoWidth, box.height / video.videoHeight);
    const drawnW = video.videoWidth * scale, drawnH = video.videoHeight * scale;
    const left = box.left + (box.width - drawnW) / 2, top = box.top + (box.height - drawnH) / 2;
    const px = (clientX - left) / drawnW, py = (clientY - top) / drawnH;
    if (px >= 0 && px <= 1 && py >= 0 && py <= 1) { fx = px; fy = py; }
  }

  const hit = paneLayout(count).findIndex(([x, y, w, h]) =>
    fx >= x && fx < x + w && fy >= y && fy < y + h);
  // A three-pane grid leaves two corners empty below the top row; a tap there means nothing.
  return hit;
}

// Live HLS stalls for a moment now and then at the live edge. Only say so if it lasts, and
// then only as a small badge — blanking the picture over a half-second hiccup reads as broken.
video.addEventListener('waiting', () => {
  clearTimeout(setOverlay.pending);
  setOverlay.pending = setTimeout(() => {
    if (video.readyState < 3 && !video.paused) setOverlay('Buffering…', true);
  }, 900);
});
video.addEventListener('timeupdate', renderScrub);
video.addEventListener('progress', renderScrub);

/// Poll the session so a transcoder that dies is reported rather than looking like a stall.
function watchSession() {
  clearInterval(sessionWatch);
  sessionWatch = setInterval(async () => {
    if (!state.play) return;
    try {
      const { body } = await api(`/api/session/${state.play.sessionId}`);
      if (body.finished && body.exitCode !== 0 && video.paused && video.currentTime === 0) {
        setOverlay(`Could not play this program. ${body.lastError || 'The transcoder stopped.'}`);
      }
    } catch {
      // The session is gone. If we are still playing buffered video, say nothing.
      if (video.paused) setOverlay('Could not play this program. The playback session ended.');
    }
  }, 10000);
}

function detachPlayer() {
  if (hls) { hls.destroy(); hls = null; }
  video.removeAttribute('src');
  video.load();
  resetCaptions();
}

// ---------------------------------------------------------------------- captions

/* Broadcast closed captions are EIA-608 data carried inside the video itself, not a separate
   track, and they survive the transcode: ffmpeg lifts them out of the MPEG-2 user data and
   every encoder here (libx264, h264_nvenc, h264_vaapi) writes them back as A/53 SEI by
   default, which hls.js then decodes into a text track. Hardware *decoding* is the one thing
   that loses them — mpeg2_cuvid never exports the side data, so there is nothing left to
   re-embed — which makes TABLOWEB_HWDECODE and captions mutually exclusive.
   The FAST channels are different again: their captions come from the CDN as subtitle
   renditions in the manifest, which hls.js only renders once one is selected.

   Nothing shows any of this by default, so the button below is the whole feature: the track
   exists but starts disabled, and the browser's own controls expose it inconsistently —
   Chrome buries it, Firefox omits it entirely. */

const CC_KEY = 'tabloweb.cc';
let ccOn = localStorage.getItem(CC_KEY) === '1';

const isCaptionTrack = (t) => t.kind === 'captions' || t.kind === 'subtitles';

function captionTracks() {
  return Array.from(video.textTracks).filter(isCaptionTrack);
}

/* Apply the current preference to whatever tracks exist now. Tracks appear part-way through
   playback (hls.js only creates one when it first sees caption data), so this runs again on
   every addtrack rather than once at start-up.

   'hidden' rather than 'disabled' for the off state: a disabled track is not parsed at all,
   and a viewer turning captions on mid-programme would then wait for the next caption to
   arrive before seeing anything. */
function applyCaptions() {
  const tracks = captionTracks();
  tracks.forEach((t, i) => { t.mode = ccOn && i === 0 ? 'showing' : 'hidden'; });

  // Manifest subtitle renditions (the FAST channels) are hls.js's own selection, separate
  // from the text tracks above.
  if (hls && hls.subtitleTracks && hls.subtitleTracks.length) {
    hls.subtitleDisplay = ccOn;
    hls.subtitleTrack = ccOn ? 0 : -1;
  }

  const available = tracks.length > 0 || !!(hls && hls.subtitleTracks && hls.subtitleTracks.length);
  const btn = $('ccBtn');
  btn.hidden = !available;
  btn.setAttribute('aria-pressed', String(ccOn));
  btn.title = ccOn ? 'Turn closed captions off' : 'Turn closed captions on';
}

function resetCaptions() {
  $('ccBtn').hidden = true;
  captionTracks().forEach(t => { t.mode = 'disabled'; });
}

video.textTracks.addEventListener('addtrack', (e) => {
  if (isCaptionTrack(e.track)) applyCaptions();
});

$('ccBtn').addEventListener('click', () => {
  ccOn = !ccOn;
  localStorage.setItem(CC_KEY, ccOn ? '1' : '0');
  applyCaptions();
});

async function stopSession() {
  startTicket++;                // anything mid-start is now superseded
  clearInterval(sessionWatch);
  const play = state.play;
  state.play = null;
  detachPlayer();
  if (play && play.sessionId) {
    try { await fetch(`/api/stop/${play.sessionId}`, { method: 'POST' }); } catch { /* best effort */ }
  }
}

function closePlayer() {
  $('player').hidden = true;
  $('scrim').hidden = true;
  // A mosaic is not a StreamManager session, so stopSession() below would leave it running —
  // with every tuner in the box — until the idle reaper noticed.
  if (mv.session) teardownMosaic(true);
  state.media = null;
  stopSession();
}

$('playerClose').addEventListener('click', closePlayer);
$('scrim').addEventListener('click', closePlayer);

// Full screen, matching the desktop app: double-click the picture or press F. The native video
// controls have their own button too; this just means the same habits work in both places.
function toggleFullscreen() {
  if (document.fullscreenElement) { document.exitFullscreen(); return; }
  const stage = $('playerStage');
  if (stage.requestFullscreen) stage.requestFullscreen();
  else if (video.webkitEnterFullscreen) video.webkitEnterFullscreen();   // iOS Safari
}

$('playerStage').addEventListener('dblclick', toggleFullscreen);

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (!$('sheet').hidden) closeSheet();
    // Leaving full screen is the browser's own job for that first Escape.
    else if (!$('player').hidden && !document.fullscreenElement) closePlayer();
    return;
  }

  if ($('player').hidden) return;
  const typing = /^(INPUT|SELECT|TEXTAREA)$/.test(document.activeElement?.tagName || '');

  if ((e.key === 'f' || e.key === 'F') && !typing) {
    e.preventDefault();
    toggleFullscreen();
  } else if (e.key === ' ' && !typing) {
    e.preventDefault();
    video.paused ? video.play() : video.pause();
  }
});

// The custom scrubber lives outside the video, so in full screen the native controls are all
// there is — fine for live, and for a recording they cover the transcoded part.
document.addEventListener('fullscreenchange', () => {
  $('player').classList.toggle('fullscreen', !!document.fullscreenElement);
  dockMosaicBar(!!document.fullscreenElement && !!mv.session);
});

/* Full screen shows one element and nothing else on the page, and #mosaicBar is not inside it —
   so the pane buttons vanish exactly when they are the only control left, because the arrow keys
   are not an option on a touchscreen. Move the bar into the stage for the duration rather than
   duplicating it, and put it back where the layout expects it afterwards. */
function dockMosaicBar(intoStage) {
  const bar = $('mosaicBar');
  const stage = $('playerStage');
  if (intoStage) {
    if (bar.parentElement !== stage) stage.append(bar);
  } else if (bar.parentElement === stage) {
    // Its home is the player, immediately before the scrubber.
    $('player').insertBefore(bar, $('scrub'));
  }
}

// A tuner stays busy while anything pulls segments, so tell the server on the way out.
window.addEventListener('pagehide', () => {
  if (state.play) navigator.sendBeacon(`/api/stop/${state.play.sessionId}`);
  // A mosaic holds every tuner, so waiting 90s for the idle reaper is worth avoiding.
  if (mv.session) navigator.sendBeacon(`/api/mosaic/${mv.session.id}/stop`);
});

// ---------------------------------------------------------------------------- scrubber

/* A recording's HLS playlist only covers the part ffmpeg has transcoded, starting at the
   session's offset. The bar below therefore shows the whole recording, with a lighter band
   for the region that is transcoded and instantly seekable. Seeking inside that band is a
   normal seek; seeking outside it restarts the transcode at the new position. */

function renderScrub() {
  const media = state.media;
  if (!media || media.live || !media.duration) return;
  const play = state.play;

  // Between a seek and the new transcode being ready there is no session, so fall back to
  // where we asked to go rather than snapping the bar back to the start.
  const at = play ? play.offset + (video.currentTime || 0) : state.pendingPosition;
  const readyFrom = (play ? play.offset : state.pendingPosition) / media.duration;
  const readyTo = play
    ? Math.min(1, (play.offset + (video.duration || 0)) / media.duration)
    : readyFrom;

  $('scrubReady').style.left = `${readyFrom * 100}%`;
  $('scrubReady').style.width = `${Math.max(0, readyTo - readyFrom) * 100}%`;
  $('scrubPlayed').style.width = `${Math.min(100, (at / media.duration) * 100)}%`;
  $('scrubKnob').style.left = `${Math.min(100, (at / media.duration) * 100)}%`;
  $('scrubAt').textContent = fmtPosition(at);
  $('scrubOf').textContent = fmtPosition(media.duration);
}

$('scrubBar').addEventListener('click', (event) => {
  const media = state.media;
  if (!media || media.live || !media.duration) return;

  const box = event.currentTarget.getBoundingClientRect();
  const target = Math.max(0, Math.min(1, (event.clientX - box.left) / box.width)) * media.duration;

  // Inside what has already been transcoded? Then it is just a seek.
  if (state.play) {
    const localTime = target - state.play.offset;
    if (localTime >= 0 && video.duration && localTime <= video.duration - 1) {
      video.currentTime = localTime;
      renderScrub();
      return;
    }
  }
  startSession(media.path, false, target, media.duration);
});

// -------------------------------------------------------------------------- start-up

// Who is signed in. With TABLOWEB_NO_LOGIN there is nobody to name, so the chip stays hidden
// rather than showing an empty one.
(async () => {
  try {
    const { body } = await api('/api/me');
    if (!body.loginRequired || !body.name) return;
    $('whoName').textContent = body.name;
    $('whoName').title = body.device ? `${body.name} · ${body.device}` : body.name;
    $('who').hidden = false;
  } catch { /* the page works fine without it */ }
})();

$('signOut').addEventListener('click', async (event) => {
  event.preventDefault();
  // Hold shift to forget the Tablo credentials on the server as well, so the next visitor is
  // asked for an account rather than finding this one already connected.
  const forget = event.shiftKey;
  await fetch(`/api/signout?forget=${forget}`, { method: 'POST' });
  location.href = '/login';
});

// -------------------------------------------------------------------------- updates

// Only the Windows install can actually apply an update (msiexec, via a Windows Service) — on
// Docker or a Linux checkout the endpoint reports `available: false` and this stays quiet.
async function checkForUpdate() {
  try {
    const { body } = await api('/api/update');
    if (!body.available || !body.latestVersion) { $('updateBanner').hidden = true; return; }
    if (localStorage.getItem('tabloweb.updateDismissed') === body.latestVersion) return;

    $('updateBanner').dataset.version = body.latestVersion;
    $('updateText').textContent =
      `TabloWeb ${body.latestVersion} is available (you have ${body.currentVersion}).`;
    $('updateBtn').disabled = false;
    $('updateBtn').textContent = 'Update now';
    $('updateBanner').hidden = false;
  } catch { /* not worth interrupting anyone's TV over */ }
}

$('updateBtn').addEventListener('click', async () => {
  $('updateBtn').disabled = true;
  $('updateBtn').textContent = 'Installing…';
  $('updateText').textContent =
    'Downloading and installing the update — this page will go quiet for a minute while the ' +
    'service restarts.';
  try {
    await api('/api/update/install', { method: 'POST' });
  } catch (err) {
    $('updateText').textContent = `Update failed: ${err.message}`;
    $('updateBtn').disabled = false;
    $('updateBtn').textContent = 'Update now';
  }
});

$('updateDismiss').addEventListener('click', () => {
  const version = $('updateBanner').dataset.version;
  try { if (version) localStorage.setItem('tabloweb.updateDismissed', version); } catch { /* fine */ }
  $('updateBanner').hidden = true;
});

checkForUpdate();
setInterval(checkForUpdate, 3600000);

setView(location.hash.replace('#', '') || 'live');
pollStatus();
setInterval(pollStatus, 15000);

// Live listings age out; refresh them while that tab is open, and keep the heading of a
// channel that is being watched honest across a programme boundary.
setInterval(() => {
  if (state.media) refreshLiveTitle();
  else if (state.view === 'live') loadNow();
}, 60000);

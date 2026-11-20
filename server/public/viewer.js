// Hallow Assist browser viewer: see and (when they allow it) control a Windows PC that clicked "Get help".
// Works with a mouse, a touch screen (tap = click, two fingers = scroll, long press = right click, pinch = zoom)
// and TV browsers. All crypto is in proto.js (WebCrypto); the relay only passes sealed messages.
import * as P from './proto.js';
import { SCAN, SPECIAL } from './keys.js';

const $ = id => document.getElementById(id);
const st = window.__assist = { state: 'idle', frames: 0, bytes: 0, tiles: 0, safety: null, control: false, sent: [], chat: [], files: [], clip: null, end: null, monitors: [], mon: 0, banner: '' };
let link = null, canvas = $('screen'), ctx = canvas.getContext('2d'), allowed = { control: false, clipboard: false };
let view = { scale: 1, x: 0, y: 0 }, encW = 0, encH = 0;

// ---- connect page
const params = new URLSearchParams(location.search);
if (params.get('code')) $('code').value = params.get('code');
$('name').value = params.get('name') || localStorage.getItem('assist-name') || '';
$('relayAddr').textContent = location.origin;
const say = (t, bad) => { $('status').textContent = t; $('status').className = 'status' + (bad ? ' bad' : ''); };

$('form').addEventListener('submit', e => {
  e.preventDefault();
  const code = P.normCode($('code').value), password = $('password').value, name = $('name').value.trim();
  if (code.length !== 9) return say('The code has 9 digits.', true);
  if (!password.trim()) return say('Type the password they read out to you.', true);
  localStorage.setItem('assist-name', name);
  $('go').disabled = true;
  start(code, password, name || 'Helper');
});

function start(code, password, name) {
  const url = (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host + '/ws';
  link = new P.ViewerLink({ url, code, password, name, client: 'browser' });
  st.state = 'connecting'; st.end = null;
  link.onState = t => say(t);
  link.onSafety = s => { st.safety = s; $('safety').textContent = s; $('safetyBox').classList.remove('hidden'); say('Connected securely. Waiting for them to click Allow...'); st.state = 'waiting'; };
  link.onMessage = onMessage;
  link.onEnd = reason => ended(reason);
  link.start().catch(e => ended(String(e.message || e)));
}

function ended(reason) {
  st.state = 'ended'; st.end = reason;
  clearInterval(pinger);
  $('session').classList.add('hidden'); $('connect').classList.remove('hidden'); $('safetyBox').classList.add('hidden');
  $('go').disabled = false; $('password').value = '';
  say(reason || 'The session ended.', true);
  link = null;
}

// ---- messages from their PC
const send = plain => { if (link) { st.sent.push(plain[0] === P.M.JSON ? P.parseJson(plain).t : 'm' + plain[0]); if (st.sent.length > 200) st.sent.shift(); return link.send(plain); } };
const sendJson = o => send(P.jsonMsg(o));
let pinger = null, lastPing = 0, rtt = 0, frameTimes = [], byteTimes = [], quality = '';

async function onMessage(b) {
  if (b[0] === P.M.FRAME) return onFrame(b);
  if (b[0] === P.M.FILE) return onFileChunk(P.parseFileChunk(b));
  if (b[0] !== P.M.JSON) return;
  const m = P.parseJson(b);
  switch (m.t) {
    case 'consent':
      if (!m.allowed) return link.end('They said no.');
      allowed = { control: !!m.control, clipboard: !!m.clipboard };
      st.monitors = m.monitors || []; st.control = allowed.control; st.state = 'connected';
      $('who').textContent = m.host ? m.host + '’s PC' : 'Their PC';
      showSession(); break;
    case 'control': allowed.control = !!m.on; st.control = allowed.control; setBanner(m.on ? '' : 'They turned off control - you can still see their screen.'); updateButtons(); break;
    case 'clipboard': allowed.clipboard = !!m.on; updateButtons(); break;
    case 'clip': st.clip = String(m.text || ''); $('clipIn').value = st.clip; break;
    case 'state': setBanner(m.secure ? 'They need to click the admin prompt themselves - Windows hides it from remote help.' : m.elevated ? 'That window runs as administrator: you can see it, but they need to click it themselves.' : (allowed.control ? '' : 'View only - they haven’t allowed control.')); break;
    case 'monitors': st.monitors = m.monitors || []; drawMonitors(); break;
    case 'chat': addChat(m.text, false); break;
    case 'pong': rtt = Date.now() - (m.ts || 0); break;
    case 'q': quality = m.label || ''; break;
    case 'file-offer': offerIn(m); break;
    case 'file-accept': sendFile(m.id); break;
    case 'file-decline': fileNote(m.id, 'They declined it.'); outgoing.delete(m.id); break;
    case 'file-done': fileDone(m.id); break;
    case 'file-cancel': fileNote(m.id, 'Cancelled.'); incoming.delete(m.id); break;
    case 'bye': link.end(m.reason || 'They ended the session.'); break;
  }
}

function showSession() {
  $('connect').classList.add('hidden'); $('session').classList.remove('hidden');
  drawMonitors(); updateButtons(); setBanner(allowed.control ? '' : 'View only - they haven’t allowed control.');
  pinger = setInterval(() => { sendJson({ t: 'ping', ts: Date.now() }); showStats(); }, 2000);
  if (matchMedia('(orientation: portrait) and (max-width: 700px)').matches && !sessionStorage.getItem('rotated')) $('rotate').classList.remove('hidden');
  canvas.focus();
}
$('rotateOk').onclick = () => { $('rotate').classList.add('hidden'); sessionStorage.setItem('rotated', '1'); };
function setBanner(t) { st.banner = t; $('banner').textContent = t; $('banner').classList.toggle('hidden', !t); }
function drawMonitors() {
  const g = $('monitors'); g.textContent = '';
  if (st.monitors.length < 2) return;
  st.monitors.forEach((mon, i) => { const b = document.createElement('button'); b.textContent = 'Screen ' + (i + 1); b.className = i === st.mon ? 'on' : ''; b.onclick = () => { st.mon = i; sendJson({ t: 'monitor', i }); drawMonitors(); }; g.appendChild(b); });
}
let controlOn = true;
function updateButtons() {
  $('bControl').disabled = !allowed.control; $('bControl').classList.toggle('on', allowed.control && controlOn);
  $('bControl').textContent = allowed.control ? (controlOn ? 'Control on' : 'Control off') : 'View only';
  $('bClip').classList.toggle('hidden', !allowed.clipboard);
  $('bKeys').disabled = !allowed.control;
  $('bDraw').disabled = st.state !== 'connected';
}
const canControl = () => allowed.control && controlOn && st.state === 'connected';
$('bControl').onclick = () => { controlOn = !controlOn; updateButtons(); };
$('quality').onchange = () => sendJson({ t: 'quality', q: $('quality').value });
$('bEnd').onclick = () => { sendJson({ t: 'bye', reason: 'The helper ended the session.' }); setTimeout(() => link && link.end('You ended the session.'), 150); };
$('bFit').onclick = () => { view = { scale: 1, x: 0, y: 0 }; layout(); };
function showStats() {
  const now = Date.now();
  frameTimes = frameTimes.filter(t => now - t < 3000); byteTimes = byteTimes.filter(x => now - x[0] < 3000);
  const kbps = byteTimes.reduce((a, x) => a + x[1], 0) * 8 / 3 / 1000;
  $('stats').textContent = `${(frameTimes.length / 3).toFixed(0)} fps · ${kbps >= 1000 ? (kbps / 1000).toFixed(1) + ' Mbit/s' : kbps.toFixed(0) + ' kbit/s'} · ${rtt} ms${quality ? ' · ' + quality : ''} · encrypted`;
}

// ---- the picture: changed tiles as JPEGs, drawn onto the canvas
let drawing = Promise.resolve();
function onFrame(b) {
  const f = P.parseFrame(b);
  drawing = drawing.then(async () => {
    if (f.encW !== encW || f.encH !== encH) { encW = canvas.width = f.encW; encH = canvas.height = f.encH; layout(); }
    if (f.mon !== st.mon) { st.mon = f.mon; drawMonitors(); }
    const imgs = await Promise.all(f.tiles.map(t => createImageBitmap(new Blob([t.jpeg], { type: 'image/jpeg' })).catch(() => null)));
    imgs.forEach((im, i) => { if (im) { ctx.drawImage(im, f.tiles[i].x, f.tiles[i].y); im.close?.(); } });
    st.frames++; st.tiles += f.tiles.length; st.bytes += b.length; st.lastSeq = f.seq;
    frameTimes.push(Date.now()); byteTimes.push([Date.now(), b.length]);
    sendJson({ t: 'ack', seq: f.seq });
  }).catch(() => {});
}
function layout() {
  const stage = $('stage').getBoundingClientRect();
  if (!encW || !stage.width) return;
  const fit = Math.min(stage.width / encW, stage.height / encH);
  const w = encW * fit, h = encH * fit;
  canvas.style.width = w + 'px'; canvas.style.height = h + 'px';
  const ox = (stage.width - w) / 2, oy = (stage.height - h) / 2;
  view.scale = Math.max(1, Math.min(5, view.scale));
  view.x = Math.min(0, Math.max(view.x, w - w * view.scale)); view.y = Math.min(0, Math.max(view.y, h - h * view.scale));
  canvas.style.transform = `translate(${ox + view.x}px, ${oy + view.y}px) scale(${view.scale})`;
}
addEventListener('resize', layout);
addEventListener('orientationchange', () => setTimeout(layout, 200));

// position on the canvas -> 0..65535 on their monitor
function norm(clientX, clientY) {
  const r = canvas.getBoundingClientRect();
  const nx = Math.max(0, Math.min(1, (clientX - r.left) / r.width)), ny = Math.max(0, Math.min(1, (clientY - r.top) / r.height));
  return [Math.round(nx * 65535), Math.round(ny * 65535)];
}
let lastMove = 0, pendingMove = null;
function move(x, y, force) {
  if (!canControl()) return;
  const [nx, ny] = norm(x, y);
  const now = performance.now();
  if (!force && now - lastMove < 16) { pendingMove = [x, y]; return; }
  lastMove = now; pendingMove = null;
  send(P.input(P.IN.MOVE, nx, ny));
}
setInterval(() => { if (pendingMove) move(pendingMove[0], pendingMove[1], true); }, 30);
function button(btn, down, x, y) { if (!canControl()) return; const [nx, ny] = norm(x, y); send(P.input(P.IN.BUTTON, btn, down, nx, ny)); }
function click(btn, x, y) { move(x, y, true); button(btn, true, x, y); button(btn, false, x, y); }

// mouse (desktop and TV browsers)
canvas.addEventListener('pointermove', e => { if (drawMode) return; if (e.pointerType === 'mouse') move(e.clientX, e.clientY); });
canvas.addEventListener('pointerdown', e => { if (drawMode || e.pointerType !== 'mouse') return; canvas.focus(); e.preventDefault(); move(e.clientX, e.clientY, true); button(e.button === 2 ? 1 : e.button === 1 ? 2 : 0, true, e.clientX, e.clientY); });
canvas.addEventListener('pointerup', e => { if (drawMode || e.pointerType !== 'mouse') return; e.preventDefault(); button(e.button === 2 ? 1 : e.button === 1 ? 2 : 0, false, e.clientX, e.clientY); });
canvas.addEventListener('contextmenu', e => e.preventDefault());
canvas.addEventListener('wheel', e => { e.preventDefault(); if (!canControl()) return; const notch = v => Math.max(-32767, Math.min(32767, Math.round(-v / (e.deltaMode === 1 ? 3 : 100) * 120))); send(P.input(P.IN.WHEEL, notch(e.deltaY), -notch(e.deltaX))); }, { passive: false });

// touch: tap = click, long press = right click, one-finger drag = drag, two fingers = scroll, pinch = zoom
let touch = null;
const mid = t => [(t[0].clientX + t[1].clientX) / 2, (t[0].clientY + t[1].clientY) / 2];
const dist = t => Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
canvas.addEventListener('touchstart', e => {
  e.preventDefault();
  if (drawMode) return;
  const t = e.touches;
  if (t.length === 1) {
    const x = t[0].clientX, y = t[0].clientY;
    touch = { mode: 'tap', x, y, sx: x, sy: y, timer: setTimeout(() => { if (touch && touch.mode === 'tap') { touch.mode = 'done'; click(1, x, y); if (navigator.vibrate) navigator.vibrate(30); } }, 550) };
  } else if (t.length === 2) {
    if (touch) { clearTimeout(touch.timer); if (touch.mode === 'drag') button(0, false, touch.x, touch.y); }
    const [mx, my] = mid(t);
    touch = { mode: 'two', mx, my, d: dist(t), acc: 0, scale0: view.scale, vx: view.x, vy: view.y, pinch: false };
  }
}, { passive: false });
canvas.addEventListener('touchmove', e => {
  e.preventDefault();
  if (drawMode) return;
  if (!touch) return;
  const t = e.touches;
  if (touch.mode === 'tap' || touch.mode === 'drag') {
    const x = t[0].clientX, y = t[0].clientY;
    if (touch.mode === 'tap' && Math.hypot(x - touch.sx, y - touch.sy) > 12) { clearTimeout(touch.timer); touch.mode = 'drag'; move(touch.sx, touch.sy, true); button(0, true, touch.sx, touch.sy); }
    if (touch.mode === 'drag') { touch.x = x; touch.y = y; move(x, y); }
  } else if (touch.mode === 'two' && t.length === 2) {
    const [mx, my] = mid(t), d = dist(t);
    if (!touch.pinch && Math.abs(d - touch.d) > 40) touch.pinch = true;
    if (touch.pinch) { view.scale = touch.scale0 * d / touch.d; view.x = touch.vx + (mx - touch.mx); view.y = touch.vy + (my - touch.my); layout(); }
    else if (canControl()) {
      touch.acc += my - touch.my; touch.my = my;
      while (Math.abs(touch.acc) >= 30) { const dir = Math.sign(touch.acc); send(P.input(P.IN.WHEEL, dir * 120, 0)); touch.acc -= dir * 30; }
    }
  }
}, { passive: false });
canvas.addEventListener('touchend', e => {
  e.preventDefault();
  if (drawMode) return;
  if (!touch) return;
  if (e.touches.length > 0 && touch.mode === 'two') return;
  clearTimeout(touch.timer);
  if (touch.mode === 'tap') click(0, touch.sx, touch.sy);
  else if (touch.mode === 'drag') { move(touch.x, touch.y, true); button(0, false, touch.x, touch.y); }
  touch = null;
}, { passive: false });

// v1.2: draw on their screen (mouse, finger or pen): lines show over their screen and fade after a few seconds.
// Allowed in view-only sessions too - drawing can't click anything. Right-click or "Clear" removes the lines.
let drawMode = false, stroke = null, strokeIds = Math.floor(Math.random() * 1e9);
st.drawn = 0;
function flushStroke() {
  if (!stroke || stroke.pts.length < 2) return;
  sendJson({ t: 'draw', id: stroke.id, mon: st.mon, pts: stroke.pts }); st.drawn++;
  const n = stroke.pts.length; stroke.pts = [stroke.pts[n - 2], stroke.pts[n - 1]];
}
function endStroke() { if (!stroke) return; flushStroke(); clearInterval(stroke.timer); stroke = null; }
$('bDraw').onclick = () => { drawMode = !drawMode; $('bDraw').classList.toggle('on', drawMode); $('bDraw').textContent = drawMode ? 'Drawing - tap to stop' : 'Draw'; $('bDrawClear').classList.toggle('hidden', !drawMode); canvas.style.cursor = drawMode ? 'crosshair' : ''; if (!drawMode) endStroke(); };
$('bDrawClear').onclick = () => sendJson({ t: 'draw-clear' });
canvas.addEventListener('pointerdown', e => {
  if (!drawMode || st.state !== 'connected') return;
  e.preventDefault();
  if (e.button === 2) { sendJson({ t: 'draw-clear' }); return; }
  endStroke(); try { canvas.setPointerCapture(e.pointerId); } catch { }
  stroke = { id: ++strokeIds, pts: norm(e.clientX, e.clientY), timer: setInterval(flushStroke, 50) };
});
canvas.addEventListener('pointermove', e => { if (!drawMode || !stroke) return; e.preventDefault(); stroke.pts.push(...norm(e.clientX, e.clientY)); if (stroke.pts.length >= 600) flushStroke(); });
const stopStroke = e => { if (!drawMode) return; e.preventDefault(); endStroke(); };
canvas.addEventListener('pointerup', stopStroke); canvas.addEventListener('pointercancel', stopStroke);

// physical keyboard
const held = new Set();
function key(code, down) { const s = SCAN[code]; if (!s || !canControl()) return false; send(P.input(P.IN.KEY, s, down)); if (down) held.add(code); else held.delete(code); return true; }
addEventListener('keydown', e => {
  if (st.state !== 'connected' || e.target.closest('input, textarea:not(#kbd), select')) return;
  if (e.target === $('kbd') && (!e.code || e.key === 'Unidentified' || e.keyCode === 229)) return;   // phone keyboards: handled by the input event
  if (key(e.code, true)) e.preventDefault();
});
addEventListener('keyup', e => { if (st.state !== 'connected' || e.target.closest('input, textarea:not(#kbd), select')) return; if (key(e.code, false)) e.preventDefault(); });
addEventListener('blur', () => { for (const c of [...held]) key(c, false); });   // never leave a key stuck down on their PC

// on-screen keyboard: the phone's own keyboard types text; a row of special keys (Ctrl/Alt/Win/Shift stick until the next key)
const sticky = new Set();
$('kbd').addEventListener('input', e => {
  if (!canControl()) return;
  if (e.inputType === 'deleteContentBackward') tap('Backspace');
  else if (e.inputType === 'insertLineBreak') tap('Enter');
  else if (e.data) { if (sticky.size) { for (const ch of e.data) { const c = /[a-z]/i.test(ch) ? 'Key' + ch.toUpperCase() : /\d/.test(ch) ? 'Digit' + ch : null; if (c) tap(c); } } else send(P.input(P.IN.TEXT, e.data)); }
  $('kbd').value = '';
});
function tap(code) { key(code, true); key(code, false); for (const m of [...sticky]) { key(m, false); sticky.delete(m); } drawKeys(); }
function drawKeys() {
  const row = $('keyrow'); row.textContent = '';
  for (const [label, code, mod] of SPECIAL) {
    const b = document.createElement('button'); b.textContent = label; b.className = sticky.has(code) ? 'on' : '';
    b.onclick = () => { if (mod) { if (sticky.has(code)) { key(code, false); sticky.delete(code); } else { key(code, true); sticky.add(code); } drawKeys(); } else tap(code); $('kbd').focus(); };
    row.appendChild(b);
  }
}
$('bKeys').onclick = () => { const r = $('keyrow'); const show = r.classList.contains('hidden'); r.classList.toggle('hidden', !show); $('bKeys').classList.toggle('on', show); if (show) { drawKeys(); $('kbd').focus(); } else $('kbd').blur(); setTimeout(layout, 50); };

// ---- panels
const panels = ['pChat', 'pClip', 'pFiles'];
function openPanel(id) { for (const p of panels) $(p).classList.toggle('hidden', p !== id || !$(p).classList.contains('hidden')); if (id === 'pChat') $('chatDot').classList.add('hidden'); }
$('bChat').onclick = () => openPanel('pChat'); $('bClip').onclick = () => openPanel('pClip'); $('bFiles').onclick = () => openPanel('pFiles');
document.querySelectorAll('[data-close]').forEach(b => b.onclick = () => $(b.dataset.close).classList.add('hidden'));
function addChat(text, me) {
  st.chat.push({ me, text });
  const d = document.createElement('div'); d.className = me ? 'me' : ''; d.textContent = (me ? 'You: ' : 'Them: ') + text; $('chatLog').appendChild(d); $('chatLog').scrollTop = 1e9;
  if (!me && $('pChat').classList.contains('hidden')) $('chatDot').classList.remove('hidden');
}
$('chatForm').addEventListener('submit', e => { e.preventDefault(); const t = $('chatText').value.trim(); if (!t) return; sendJson({ t: 'chat', text: t }); addChat(t, true); $('chatText').value = ''; });
$('clipSend').onclick = () => { const t = $('clipOut').value; if (t && allowed.clipboard) { sendJson({ t: 'clip', text: t.slice(0, 1000000) }); $('clipOut').value = ''; } };
$('clipCopy').onclick = () => { if (navigator.clipboard && st.clip) navigator.clipboard.writeText(st.clip).catch(() => {}); };

// ---- files both ways (the receiver is always asked first)
const outgoing = new Map(), incoming = new Map(); let nextId = 1;
const size = n => n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : n < 1073741824 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1073741824).toFixed(2) + ' GB';
function fileNote(id, text) { const el = document.getElementById('f' + id); if (el) el.textContent = el.dataset.name + ' - ' + text; st.files.push({ id, text }); }
function fileRow(id, name, text) { const d = document.createElement('div'); d.id = 'f' + id; d.dataset.name = name; d.textContent = name + ' - ' + text; $('fileList').appendChild(d); return d; }
$('filePick').onchange = () => {
  const f = $('filePick').files[0]; $('filePick').value = '';
  if (!f) return;
  const id = nextId++; outgoing.set(id, f);
  fileRow(id, f.name, 'waiting for them to accept...');
  sendJson({ t: 'file-offer', id, name: f.name, size: f.size });
};
async function sendFile(id) {
  const f = outgoing.get(id); if (!f) return;
  const CH = 64 * 1024;
  for (let off = 0; off < f.size; off += CH) {
    if (!link) return;
    const part = new Uint8Array(await f.slice(off, off + CH).arrayBuffer());
    await send(P.fileChunk(id, part));
    while (link && link.ws.bufferedAmount > 2 * 1024 * 1024) await new Promise(r => setTimeout(r, 20));
    if (off % (CH * 32) === 0) fileNote(id, 'sending ' + Math.round(off / f.size * 100) + '%');
  }
  sendJson({ t: 'file-done', id, size: f.size }); fileNote(id, 'sent'); outgoing.delete(id);
}
const MAX_IN = 512 * 1024 * 1024;
function offerIn(m) {
  const id = m.id, name = String(m.name || 'file').replace(/[\\/:*?"<>|]/g, '_').slice(0, 120), sz = Number(m.size) || 0;
  for (const p of panels) $(p).classList.toggle('hidden', p !== 'pFiles');
  if (sz > MAX_IN) { sendJson({ t: 'file-decline', id }); fileRow(id, name, 'too big for a browser (over 512 MB) - declined'); return; }
  ask(`They want to send you ${name} (${size(sz)}).`, ok => {
    if (!ok) { sendJson({ t: 'file-decline', id }); fileRow(id, name, 'declined'); return; }
    incoming.set(id, { name, size: sz, parts: [], got: 0 }); fileRow(id, name, 'receiving...');
    sendJson({ t: 'file-accept', id });
  });
}
function onFileChunk({ id, data }) { const f = incoming.get(id); if (!f) return; f.parts.push(data.slice()); f.got += data.length; if (f.got > f.size || f.got > MAX_IN) { incoming.delete(id); sendJson({ t: 'file-cancel', id }); fileNote(id, 'more data than offered - stopped'); } }
function fileDone(id) {
  const f = incoming.get(id); if (!f) return; incoming.delete(id);
  if (f.got !== f.size) return fileNote(id, 'arrived incomplete');
  const url = URL.createObjectURL(new Blob(f.parts));
  const el = document.getElementById('f' + id); el.textContent = '';
  const a = document.createElement('a'); a.href = url; a.download = f.name; a.textContent = 'Save ' + f.name + ' (' + size(f.size) + ')'; el.appendChild(a);
  st.files.push({ id, text: 'received', size: f.got });
}
let promptCb = null;
function ask(text, cb) { $('promptText').textContent = text; $('prompt').classList.remove('hidden'); promptCb = cb; }
$('promptYes').onclick = () => { $('prompt').classList.add('hidden'); const c = promptCb; promptCb = null; c && c(true); };
$('promptNo').onclick = () => { $('prompt').classList.add('hidden'); const c = promptCb; promptCb = null; c && c(false); };
st.answerPrompt = ok => (ok ? $('promptYes') : $('promptNo')).click();   // tests

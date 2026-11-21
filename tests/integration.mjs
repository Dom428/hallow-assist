// Hallow Assist integration tests: a local relay on 127.0.0.1, two HallowAssist.exe copies in test mode (own data folders and
// pipes, a synthetic screen instead of the real one, recorded input instead of SendInput, no windows), a browser-style viewer
// (proto.js on Node's WebCrypto) talking to the Windows app (CNG), and the real viewer page in headless Edge.
//   node tests/integration.mjs <work folder>
import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import dgram from 'node:dgram';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { createRelay } from '../server/relay.js';
import * as P from '../server/public/proto.js';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const EXE = path.join(ROOT, 'HallowAssist.exe');
const WORK = path.resolve(process.argv[2] || path.join(ROOT, 'tests', 'out'));
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const sleep = ms => new Promise(r => setTimeout(r, ms));
let passed = 0, failed = 0;
const results = [];
async function step(name, f) {
  try { await f(); passed++; results.push('ok   ' + name); console.log('ok   ' + name); }
  catch (e) { failed++; results.push('FAIL ' + name + ': ' + (e && e.message)); console.log('FAIL ' + name + '\n     ' + (e && e.stack || e)); }
}
async function until(f, ms = 10000, what = 'condition') { const t0 = Date.now(); for (;;) { const v = await f(); if (v) return v; if (Date.now() - t0 > ms) throw new Error('timed out waiting for ' + what); await sleep(100); } }

// ---- relay with a tap on what it forwards
const forwarded = [];
const relay = createRelay({ log: false, codeTtlMs: 600000, onForward: (role, data) => forwarded.push({ role, data: Buffer.from(data) }) });
const port = await relay.listen(0, '127.0.0.1');
const RELAY = `http://127.0.0.1:${port}`;
console.log('relay on', RELAY);

// ---- v1.4: a local UDP listener stands in for the sleeping PC's network card (Wake-on-LAN); test mode only ever sends there
const wolPackets = [];
const wol = dgram.createSocket('udp4'); wol.on('message', m => wolPackets.push(Buffer.from(m)));
await new Promise(r => wol.bind(0, '127.0.0.1', r));
const WOL_PORT = wol.address().port;

// ---- app instances
const dirs = { host: path.join(WORK, 'host'), viewer: path.join(WORK, 'viewer') };
for (const d of Object.values(dirs)) { fs.rmSync(d, { recursive: true, force: true }); fs.mkdirSync(d, { recursive: true }); }
// v1.5: a stand-in for Hallow Keys' command line (tests\standin\hk.exe, built by build.ps1 -Tests) - the real Hallow Keys is never used
const HK = path.join(ROOT, 'tests', 'standin', 'hk.exe'), HK_ITEMS = path.join(WORK, 'hk-items.txt'), HK_LOG = path.join(WORK, 'hk-calls.log');
fs.writeFileSync(HK_ITEMS, ''); fs.writeFileSync(HK_LOG, '');
const env = role => ({ ...process.env, HALLOW_ASSIST_TESTDIR: dirs[role], HALLOW_ASSIST_FAKE: '1', HALLOW_ASSIST_HEADLESS: '1', HALLOW_ASSIST_RELAY: RELAY, HALLOW_ASSIST_WOL_PORT: String(WOL_PORT),
  HALLOW_ASSIST_HK: HK, HK_STANDIN_ITEMS: HK_ITEMS, HK_STANDIN_LOG: HK_LOG, HALLOW_ASSIST_HK_TIMEOUT: '4' });
const procs = {};
function launch(role) { procs[role] = spawn(EXE, ['--tray'], { env: env(role), stdio: 'ignore', windowsHide: true }); procs[role].on('exit', c => { procs[role + 'Exit'] = c; }); }
function cmd(role, line) {
  const r = spawnSync(EXE, ['--cmd', line], { env: env(role), encoding: 'utf8', windowsHide: true, timeout: 15000 });
  return (r.stdout || '').trim();
}
const okJson = (r) => { assert.match(r, /^ok/, r); return r.length > 3 ? JSON.parse(r.slice(3)) : null; };
const status = role => okJson(cmd(role, 'status'));
const T = role => status(role).test;
const inputLog = () => { const f = path.join(dirs.host, 'input-log.jsonl'); return fs.existsSync(f) ? fs.readFileSync(f, 'utf8').trim().split('\n').filter(Boolean).map(l => JSON.parse(l)) : []; };
const hostSealed = () => forwarded.filter(f => f.role === 'host' && f.data[0] === P.T.SEALED).length;

launch('host'); launch('viewer');
await until(() => cmd('host', 'ping').startsWith('ok') && cmd('viewer', 'ping').startsWith('ok'), 15000, 'both apps');
// the steps below check what passes through the relay, so they run relay-only; the direct step switches direct on
cmd('host', 'test-direct relay'); cmd('viewer', 'test-direct relay');

async function newHostSession() {
  if (T('host').host && T('host').host !== 'Ended') { cmd('host', 'test-host-end'); await sleep(300); }
  assert.equal(cmd('host', 'test-gethelp'), 'ok');
  return await until(() => { const r = cmd('host', 'test-code'); return r.startsWith('ok') ? JSON.parse(r.slice(3)) : null; }, 10000, 'code');
}
async function connectViewer(code, password, name = 'Test Helper') {
  okJson(cmd('viewer', `test-connect ${code} ${password} ${name}`));
}

await step('family link: ping, status, menu; no session at start; unattended off by default; not starting with Windows', async () => {
  const appVersion = fs.readFileSync(path.join(ROOT, 'src', 'App.cs'), 'utf8').match(/Version = "([^"]+)";/)[1];
  assert.deepEqual(okJson(cmd('host', 'ping')), { app: 'Assist', version: appVersion });
  const s = status('host');
  assert.equal(s.app, 'Assist'); assert.equal(s.session, 'none'); assert.equal(s.unattended, false); assert.equal(s.test.startsWithWindows, false);
  const m = okJson(cmd('host', 'menu'));
  assert.deepEqual(m.items.map(i => i.id), ['gethelp', 'helpsomeone', '-', 'disconnect']);
  assert.match(cmd('host', 'invoke disconnect'), /^error/);
  assert.equal(cmd('host', 'gethelp'), 'error unknown command');   // there's no command that starts a session
  assert.ok(!JSON.stringify(s).match(/password/i));
});

let sess;
await step('Get help gives a 9-digit code and a 6-character password; status never contains them', async () => {
  sess = await newHostSession();
  assert.match(sess.code, /^\d{9}$/); assert.match(sess.password, /^[2-9A-HJKMNP-Z]{6}$/);
  const raw = fs.readFileSync(path.join(dirs.host, 'status.json'), 'utf8');
  assert.ok(!raw.includes(sess.password)); assert.equal(status('host').session, 'waiting');
});

await step('consent required: nothing is shared while the question is open; viewer sees the safety code', async () => {
  await connectViewer(sess.code, sess.password.toLowerCase());
  await until(() => T('host').consentPending, 10000, 'consent question');
  await sleep(800);
  assert.equal(hostSealed(), 0, 'host sent nothing sealed before Allow');
  const v = T('viewer'); assert.equal(v.viewer, 'Waiting'); assert.equal(v.frames, 0); assert.match(v.viewerSafety, /^\d{4}$/);
  assert.equal(T('host').helper, 'Test Helper'); assert.equal(T('host').safety, v.viewerSafety);
  assert.equal(inputLog().length, 0);
  assert.equal(T('host').bar, false);
});

await step('Allow (with control and clipboard): frames arrive, both monitors listed, bar shows, panic hotkey armed', async () => {
  assert.equal(cmd('host', 'test-consent allow control clipboard'), 'ok');
  await until(() => T('viewer').frames > 0, 10000, 'frames');
  const v = T('viewer'); assert.equal(v.viewer, 'Connected'); assert.equal(v.monitors, 2); assert.equal(v.controlAllowed, true); assert.equal(v.clipboardAllowed, true);
  assert.equal(v.screenW, 1280); assert.equal(v.screenH, 720);
  const h = T('host'); assert.equal(h.bar, true); assert.equal(h.hotkey, true); assert.equal(h.screen, 'test');
  const hash = okJson(cmd('viewer', 'test-view-hash')); assert.ok(hash.mean > 5, 'the picture is not empty');
});

await step('input: mouse mapped per monitor (incl. negative coordinates), clicks, keys by scan code, text', async () => {
  cmd('viewer', 'test-view-move 32768 32768');
  await until(() => inputLog().some(e => e.t === 'move'), 5000, 'move');
  assert.deepEqual(inputLog().find(e => e.t === 'move'), { t: 'move', x: 640, y: 360 });
  cmd('viewer', 'test-view-monitor 1');
  await until(() => T('viewer').monitor === 1, 5000, 'monitor switch');
  cmd('viewer', 'test-view-move 0 0'); cmd('viewer', 'test-view-move 65535 65535');
  await until(() => inputLog().filter(e => e.t === 'move').length >= 3, 5000, 'moves');
  const moves = inputLog().filter(e => e.t === 'move');
  assert.deepEqual(moves[1], { t: 'move', x: -1024, y: -200 }); assert.deepEqual(moves[2], { t: 'move', x: -1, y: 567 });
  assert.equal(T('viewer').screenW, 1024);
  cmd('viewer', 'test-view-monitor 0'); await until(() => T('viewer').monitor === 0, 5000, 'back to monitor 1');
  cmd('viewer', 'test-view-click 0 0');
  cmd('viewer', 'test-view-key 30');       // A
  cmd('viewer', 'test-view-key 57421');    // 0xE04D right arrow
  cmd('viewer', 'test-view-text h\u00e9');
  await until(() => inputLog().some(e => e.t === 'text'), 5000, 'text');
  const log = inputLog();
  assert.ok(log.some(e => e.t === 'button' && e.b === 0 && e.down) && log.some(e => e.t === 'button' && e.b === 0 && !e.down), 'click');
  assert.ok(log.some(e => e.t === 'key' && e.scan === 30 && e.down) && log.some(e => e.t === 'key' && e.scan === 30 && !e.down), 'key A');
  assert.ok(log.some(e => e.t === 'key' && e.scan === 0xE04D), 'extended key');
  assert.equal(log.find(e => e.t === 'text').text, 'h\u00e9');
});

await step('chat both ways', async () => {
  cmd('viewer', 'test-view-chat hello from the helper');
  cmd('host', 'test-host-chat hello back');
  await until(() => T('host').log.some(l => l.includes('chat from helper: hello from the helper')) && T('viewer').log.some(l => l.includes('chat from host: hello back')), 5000, 'chat');
});

await step('clipboard text both ways (opt-in this session)', async () => {
  cmd('viewer', 'test-view-clipsync on');
  fs.writeFileSync(path.join(dirs.host, 'clipboard.txt'), 'first');   // the first value is only remembered, never sent
  await sleep(1500);
  fs.writeFileSync(path.join(dirs.host, 'clipboard.txt'), 'CLIP-FROM-HOST');
  await until(() => T('viewer').lastClip === 'CLIP-FROM-HOST', 6000, 'clip host -> viewer');
  assert.equal(fs.readFileSync(path.join(dirs.viewer, 'clipboard.txt'), 'utf8'), 'CLIP-FROM-HOST');
  cmd('viewer', 'test-view-clip CLIP-TO-HOST');
  await until(() => fs.readFileSync(path.join(dirs.host, 'clipboard.txt'), 'utf8') === 'CLIP-TO-HOST', 5000, 'clip viewer -> host');
  cmd('host', 'test-clipboard off');
  await until(() => T('viewer').clipboardAllowed === false, 5000, 'clipboard off');
  cmd('viewer', 'test-view-clip SHOULD-NOT-ARRIVE'); await sleep(800);
  assert.equal(fs.readFileSync(path.join(dirs.host, 'clipboard.txt'), 'utf8'), 'CLIP-TO-HOST');
});

const marker = 'FILE-CONTENT-MARKER-' + 'z'.repeat(200);
await step('file transfer both ways, with a prompt on the receiving side (accept and decline)', async () => {
  const f1 = path.join(WORK, 'to-host.txt'); fs.writeFileSync(f1, marker + '\n'.repeat(3) + 'x'.repeat(200000));
  assert.equal(cmd('viewer', 'test-view-file ' + f1), 'ok');
  await until(() => T('host').filesPending === 1, 5000, 'host prompt');
  assert.ok(!fs.existsSync(path.join(dirs.host, 'Downloads', 'to-host.txt')), 'nothing saved before Accept');
  cmd('host', 'test-file-answer accept');
  const dest = path.join(dirs.host, 'Downloads', 'to-host.txt');
  await until(() => fs.existsSync(dest) && fs.statSync(dest).size === fs.statSync(f1).size, 10000, 'file on host');
  assert.equal(fs.readFileSync(dest, 'utf8'), fs.readFileSync(f1, 'utf8'));
  const f2 = path.join(WORK, 'declined.txt'); fs.writeFileSync(f2, 'no thanks');
  cmd('viewer', 'test-view-file ' + f2); await until(() => T('host').filesPending === 1, 5000, 'second prompt');
  cmd('host', 'test-file-answer decline'); await sleep(600);
  assert.ok(!fs.existsSync(path.join(dirs.host, 'Downloads', 'declined.txt')));
  assert.ok(T('viewer').log.some(l => l.includes('declined')));
  const f3 = path.join(WORK, 'to-viewer.bin'); fs.writeFileSync(f3, Buffer.from(Array.from({ length: 70000 }, (_, i) => i % 251)));
  assert.equal(cmd('host', 'test-host-file ' + f3), 'ok');
  await until(() => T('viewer').filesPending === 1, 5000, 'viewer prompt');
  cmd('viewer', 'test-file-answer accept');
  const d3 = path.join(dirs.viewer, 'Downloads', 'to-viewer.bin');
  await until(() => fs.existsSync(d3) && fs.statSync(d3).size === 70000, 10000, 'file on viewer');
  assert.ok(fs.readFileSync(d3).equals(fs.readFileSync(f3)));
});

await step('copied files (v1.5): both ways through the file transfer - one question per batch, the Mark of the Web, then on the receiver\'s clipboard; only while clipboard sharing is on', async () => {
  cmd('host', 'test-clipboard on'); await until(() => T('viewer').clipboardAllowed === true, 5000, 'clipboard sharing on');
  const src = path.join(WORK, 'copied'); fs.rmSync(src, { recursive: true, force: true }); fs.mkdirSync(path.join(src, 'a folder'), { recursive: true });
  const a = path.join(src, 'copied-one.txt'), b = path.join(src, 'copied-two.bin');
  fs.writeFileSync(a, 'COPIED-FILE-MARKER one'); fs.writeFileSync(b, Buffer.from(Array.from({ length: 90000 }, (_, i) => (i * 7) % 256)));
  fs.writeFileSync(path.join(dirs.viewer, 'clipboard-files.txt'), [a, b, path.join(src, 'a folder')].join('\r\n'));
  const p0 = T('host').filesPending;
  assert.equal(cmd('viewer', 'test-view-sendcopied'), 'ok');
  await until(() => T('host').filesPending === p0 + 1, 5000, 'the question on the PC');
  await sleep(500); assert.equal(T('host').filesPending, p0 + 1, 'one question for both files');
  assert.ok(T('host').log.some(l => l.includes('files offered: 2 (copied files)')));
  const da = path.join(dirs.host, 'Downloads', 'copied-one.txt'), db = path.join(dirs.host, 'Downloads', 'copied-two.bin');
  assert.ok(!fs.existsSync(da) && !fs.existsSync(db), 'nothing saved before Accept');
  assert.ok(T('viewer').log.some(l => l.includes("Folders aren't sent")), 'the folder is skipped, and the helper is told');
  cmd('host', 'test-file-answer accept');
  await until(() => fs.existsSync(da) && fs.existsSync(db) && fs.statSync(db).size === 90000, 10000, 'both files on the PC');
  assert.ok(fs.readFileSync(db).equals(fs.readFileSync(b))); assert.equal(fs.readFileSync(da, 'utf8'), 'COPIED-FILE-MARKER one');
  assert.match(fs.readFileSync(da + ':Zone.Identifier', 'utf8'), /ZoneId=3/, 'Mark of the Web (SmartScreen)');
  const hostClip = path.join(dirs.host, 'clipboard-files.txt');
  await until(() => fs.existsSync(hostClip), 5000, "the files on the PC's clipboard");
  assert.deepEqual(fs.readFileSync(hostClip, 'utf8').trim().split(/\r?\n/).sort(), [da, db].sort());
  // the other way: the helper asks for the files copied on the PC
  const c = path.join(src, 'from-the-pc.txt'); fs.writeFileSync(c, 'COPIED-ON-THE-PC');
  fs.writeFileSync(hostClip, c);
  const v0 = T('viewer').filesPending;
  assert.equal(cmd('viewer', 'test-view-getcopied'), 'ok');
  await until(() => T('viewer').filesPending === v0 + 1, 5000, 'the question on the helper');
  await until(() => T('host').log.some(l => l.includes('asked for the files you copied')), 5000, 'the PC is told');
  cmd('viewer', 'test-file-answer accept');
  const dc = path.join(dirs.viewer, 'Downloads', 'from-the-pc.txt');
  await until(() => fs.existsSync(dc) && fs.readFileSync(dc, 'utf8') === 'COPIED-ON-THE-PC', 10000, 'the file on the helper');
  assert.match(fs.readFileSync(dc + ':Zone.Identifier', 'utf8'), /ZoneId=3/);
  const viewClip = path.join(dirs.viewer, 'clipboard-files.txt');
  await until(() => fs.readFileSync(viewClip, 'utf8').trim() === dc, 5000, "the file on the helper's clipboard");
  // the PC sends its copied files itself (Send copied files on the red bar); declined this time
  const d2 = path.join(src, 'declined-copy.txt'); fs.writeFileSync(d2, 'no'); fs.writeFileSync(hostClip, d2);
  assert.equal(cmd('host', 'test-host-sendcopied'), 'ok');
  await until(() => T('viewer').filesPending === v0 + 1, 5000, 'the question on the helper again');
  cmd('viewer', 'test-file-answer decline'); await sleep(600);
  assert.ok(!fs.existsSync(path.join(dirs.viewer, 'Downloads', 'declined-copy.txt')), 'nothing saved after Decline');
  // clipboard sharing off: nothing either way
  cmd('host', 'test-clipboard off'); await until(() => T('viewer').clipboardAllowed === false, 5000, 'clipboard sharing off');
  assert.match(cmd('viewer', 'test-view-sendcopied'), /clipboard sharing/);
  assert.match(cmd('viewer', 'test-view-getcopied'), /^error .*clipboard sharing/);
  assert.match(cmd('host', 'test-host-sendcopied'), /clipboard sharing/);
  await sleep(500); assert.equal(T('host').filesPending, p0); assert.equal(T('viewer').filesPending, v0);
});

await step('Stop control: input is ignored from then on and the helper is told', async () => {
  cmd('host', 'test-control off');
  await until(() => T('viewer').controlAllowed === false, 5000, 'control off at the viewer');
  const n = inputLog().length;
  cmd('viewer', 'test-view-move 100 100'); await sleep(600);
  assert.equal(inputLog().length, n);
  cmd('host', 'test-control on'); await until(() => T('viewer').controlAllowed === true, 5000, 'control back');
});

await step('held keys are released when control stops', async () => {
  cmd('viewer', 'test-view-keydown 29');   // Ctrl down, never up
  await until(() => inputLog().some(e => e.t === 'key' && e.scan === 29 && e.down), 5000, 'ctrl down');
  cmd('host', 'test-control off');
  await until(() => inputLog().some(e => e.t === 'key' && e.scan === 29 && !e.down), 5000, 'ctrl released');
  cmd('host', 'test-control on'); await until(() => T('viewer').controlAllowed === true, 5000, 'control back');
});

await step('panic hotkey (Ctrl+Alt+End path) disconnects at once; the bar goes away; the helper is told', async () => {
  const before = T('host').panics;
  cmd('host', 'test-hotkey');
  await until(() => T('host').host === 'Ended', 5000, 'host ended');
  assert.equal(T('host').panics, before + 1); assert.equal(T('host').bar, false); assert.equal(T('host').hotkey, false);
  await until(() => T('viewer').viewer === 'Ended', 5000, 'viewer ended');
  assert.match(T('viewer').viewerEnd, /ended the session/);
});

await step('the relay only ever forwarded ciphertext (no chat, clipboard, file content, JPEG or names)', async () => {
  const all = Buffer.concat(forwarded.map(f => f.data));
  for (const s of ['hello from the helper', 'hello back', 'CLIP-FROM-HOST', 'CLIP-TO-HOST', 'FILE-CONTENT-MARKER', 'COPIED-FILE-MARKER', 'COPIED-ON-THE-PC', 'copied-one.txt', 'Test Helper', 'to-host.txt', 'JFIF', '"t":']) assert.equal(all.indexOf(s), -1, s);
  assert.equal(all.indexOf(Buffer.from([0xff, 0xd8, 0xff, 0xe0])), -1, 'JPEG header');
  const sealed = forwarded.filter(f => f.data[0] === P.T.SEALED);
  assert.ok(sealed.length > 20);
  const handshake = forwarded.filter(f => f.data[0] !== P.T.SEALED);
  assert.ok(handshake.every(f => [1, 2, 3, 4, 5].includes(f.data[0]) && f.data.length <= 83), 'only key agreement messages are unsealed');
  fs.writeFileSync(path.join(WORK, 'relay-forwarded-summary.txt'), forwarded.slice(0, 40).map(f => f.role + ' ' + f.data.length + ' bytes: ' + f.data.subarray(0, 24).toString('hex')).join('\n'));
});

await step('Deny: the helper is told no and no picture is ever sent', async () => {
  sess = await newHostSession();
  const before = hostSealed();
  await connectViewer(sess.code, sess.password);
  await until(() => T('host').consentPending, 10000, 'consent');
  cmd('host', 'test-consent deny');
  await until(() => T('viewer').viewer === 'Ended', 5000, 'viewer ended');
  assert.equal(T('viewer').viewerEnd, 'They said no.'); assert.equal(T('viewer').frames, 0);
  assert.equal(hostSealed() - before, 1, 'only the "no" answer');
  assert.equal(T('host').host, 'Ended');
});

await step('wrong password fails key confirmation; the host keeps waiting; 3 wrong closes the code', async () => {
  sess = await newHostSession();
  for (let i = 1; i <= 3; i++) {
    await connectViewer(sess.code, 'AAAAAA');
    await until(() => T('viewer').viewer === 'Ended', 8000, 'viewer refused');
    assert.equal(T('viewer').viewerEnd, 'Wrong password.');
    await until(() => T('host').wrong === i, 5000, 'host counted');
    if (i < 3) assert.equal(T('host').host, 'Waiting');
  }
  await until(() => T('host').host === 'Ended', 5000, 'code closed');
  assert.match(T('host').endReason, /3 times/);
});

await step('disconnect from the helper side ends the session on both', async () => {
  sess = await newHostSession();
  await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
  cmd('host', 'test-consent allow'); await until(() => T('viewer').frames > 0, 10000, 'frames');
  assert.equal(T('viewer').controlAllowed, false, 'control was not ticked');
  cmd('viewer', 'test-view-end');
  await until(() => T('host').host === 'Ended', 5000, 'host ended');
  assert.match(T('host').endReason, /helper ended/i);
});

// ---- browser-style viewer (WebCrypto) against the Windows app (CNG)
await step('WebCrypto viewer <-> CNG host: handshake, safety code, input refused before Allow, frames after, tamper ends it', async () => {
  sess = await newHostSession();
  const link = new P.ViewerLink({ url: `ws://127.0.0.1:${port}/ws`, code: sess.code, password: sess.password, name: 'Browser Helper', client: 'browser', WebSocketImpl: globalThis.WebSocket });
  const frames = []; const msgs = []; let ended = null, ready = false;
  link.onReady = () => ready = true; link.onEnd = r => ended = r;
  link.onMessage = b => { if (b[0] === P.M.FRAME) { const f = P.parseFrame(b); frames.push(f); link.send(P.jsonMsg({ t: 'ack', seq: f.seq })); } else if (b[0] === P.M.JSON) msgs.push(P.parseJson(b)); };
  await link.start();
  await until(() => ready && T('host').consentPending, 10000, 'browser viewer waiting');
  assert.equal(link.safety, T('host').safety);
  const n0 = inputLog().length;
  await link.send(P.input(P.IN.MOVE, 1000, 1000)); await link.send(P.input(P.IN.BUTTON, 0, true, 1000, 1000)); await sleep(700);
  assert.equal(inputLog().length, n0, 'input before Allow is ignored');
  cmd('host', 'test-consent allow control');
  await until(() => frames.length > 0, 10000, 'frames in the browser viewer');
  assert.equal(msgs.find(m => m.t === 'consent').monitors.length, 2);
  const f = frames[0]; assert.equal(f.encW, 1280); assert.ok(f.tiles.length > 0 && f.tiles.every(t => t.jpeg[0] === 0xff && t.jpeg[1] === 0xd8));
  await link.send(P.input(P.IN.MOVE, 65535, 0));
  await until(() => inputLog().slice(n0).some(e => e.t === 'move' && e.x === 1279 && e.y === 0), 5000, 'browser input mapped');
  // a tampered frame from the "relay": the host must refuse it and end the session
  const sealed = await link.chan.seal(P.jsonMsg({ t: 'chat', text: 'tampered' })); sealed[sealed.length - 1] ^= 1;
  link.ws.send(sealed);
  await until(() => T('host').host === 'Ended', 5000, 'host ended after tampering');
  assert.match(T('host').endReason, /security check/);
  assert.ok(!T('host').log.some(l => l.includes('tampered')));
  await until(() => ended !== null, 5000, 'browser viewer told');
});

await step('replayed sealed message from the viewer ends the session (CNG side refuses replays)', async () => {
  sess = await newHostSession();
  const link = new P.ViewerLink({ url: `ws://127.0.0.1:${port}/ws`, code: sess.code, password: sess.password, name: 'Replay', WebSocketImpl: globalThis.WebSocket });
  let ready = false; link.onReady = () => ready = true; link.onMessage = () => {}; link.onEnd = () => {};
  await link.start(); await until(() => ready && T('host').consentPending, 10000, 'waiting');
  cmd('host', 'test-consent allow');
  const m = await link.chan.seal(P.jsonMsg({ t: 'ping', ts: 1 })); link.ws.send(m); await sleep(300);
  assert.notEqual(T('host').host, 'Ended');
  link.ws.send(m);   // the same bytes again
  await until(() => T('host').host === 'Ended', 5000, 'host ended after replay');
  link.end('x');
});

await step('unattended access (opt-in): fixed code from the secret, no question asked, the bar still shows', async () => {
  const r = okJson(cmd('host', 'test-unattended-on Correct-Horse-42!'));
  assert.match(r.code, /^\d{9}$/);
  const s = status('host'); assert.equal(s.unattended, true); assert.equal(s.test.startsWithWindows, true);
  await until(() => T('host').host === 'Waiting' && T('host').unattendedHost, 10000, 'unattended waiting');
  const settings = fs.readFileSync(path.join(dirs.host, 'settings.json'), 'utf8');
  assert.ok(!settings.includes('Correct-Horse-42!'), 'the password is never stored');
  await connectViewer(r.code, 'Correct-Horse-42!', 'Me');
  await until(() => T('viewer').frames > 0, 10000, 'unattended frames');
  assert.equal(T('host').bar, true); assert.equal(T('host').consentPending, false); assert.equal(T('viewer').controlAllowed, true);
  cmd('host', 'disconnect');
  await until(() => T('viewer').viewer === 'Ended', 5000, 'viewer disconnected');
  await until(() => T('host').host === 'Waiting', 5000, 'unattended keeps waiting');
  await connectViewer(r.code, 'wrong-password-123', 'Me');
  await until(() => T('viewer').viewer === 'Ended', 8000, 'wrong unattended password'); assert.equal(T('viewer').viewerEnd, 'Wrong password.');
  assert.equal(cmd('host', 'test-unattended-off'), 'ok');
  await until(() => status('host').unattended === false && status('host').test.startsWithWindows === false, 5000, 'unattended off');
});

// ---- headless Edge: the real browser viewer page against the Windows host
async function cdp(port) {
  const list = await until(async () => { try { return await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); } catch { return null; } }, 15000, 'edge devtools');
  const page = list.find(t => t.type === 'page');
  const ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise(r => ws.onopen = r);
  let id = 0; const wait = new Map();
  ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && wait.has(m.id)) { wait.get(m.id)(m); wait.delete(m.id); } };
  const send = (method, params = {}) => new Promise(r => { const i = ++id; wait.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  const evaluate = async expr => { const r = await send('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }); if (r.result.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails)); return r.result.result.value; };
  return { send, evaluate, close: () => ws.close() };
}
await step('browser viewer in headless Edge (phone, landscape): connect, consent, picture, tap = click, long press = right click, two-finger scroll, typing', async () => {
  if (!fs.existsSync(EDGE)) throw new Error('Edge not found');
  sess = await newHostSession();
  const prof = path.join(WORK, 'edge-profile'); fs.rmSync(prof, { recursive: true, force: true });
  const dport = 9300 + Math.floor(Math.random() * 500);
  const edge = spawn(EDGE, ['--headless=new', `--remote-debugging-port=${dport}`, `--user-data-dir=${prof}`, '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--disable-sync', 'about:blank'], { stdio: 'ignore', windowsHide: true });
  try {
    const c = await cdp(dport);
    await c.send('Emulation.setDeviceMetricsOverride', { width: 844, height: 390, deviceScaleFactor: 2, mobile: true, screenOrientation: { type: 'landscapePrimary', angle: 90 } });
    await c.send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
    await c.send('Page.enable');
    await c.send('Page.navigate', { url: `${RELAY}/?code=${sess.code}` });
    await until(() => c.evaluate('!!window.__assist && document.readyState === "complete"'), 10000, 'page');
    assert.equal(await c.evaluate('document.getElementById("code").value'), sess.code);
    const shot0 = await c.send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(WORK, 'edge-connect.png'), Buffer.from(shot0.result.data, 'base64'));
    await c.evaluate(`document.getElementById("password").value=${JSON.stringify(sess.password)}; document.getElementById("name").value="Edge Helper"; document.getElementById("go").click(); true`);
    await until(() => c.evaluate('window.__assist.state === "waiting"'), 15000, 'edge waiting for consent');
    assert.equal(await c.evaluate('window.__assist.safety'), T('host').safety);
    await until(() => T('host').consentPending, 5000, 'consent');
    assert.equal(T('host').helper, 'Edge Helper');
    cmd('host', 'test-consent allow control clipboard');
    await until(() => c.evaluate('window.__assist.frames > 0'), 15000, 'frames drawn in Edge');
    await sleep(500);
    const shot = await c.send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(path.join(WORK, 'edge-session.png'), Buffer.from(shot.result.data, 'base64'));
    const rect = await c.evaluate('(() => { const r = document.getElementById("screen").getBoundingClientRect(); return { x: r.left, y: r.top, w: r.width, h: r.height }; })()');
    assert.ok(rect.w > 300, 'canvas is laid out: ' + JSON.stringify(rect));
    const n0 = inputLog().length;
    const at = (fx, fy) => ({ x: rect.x + rect.w * fx, y: rect.y + rect.h * fy });
    const touch = async (type, pts) => c.send('Input.dispatchTouchEvent', { type, touchPoints: pts.map((p, i) => ({ x: p.x, y: p.y, id: i })) });
    // tap in the middle -> left click at (640,360)
    await touch('touchStart', [at(0.5, 0.5)]); await sleep(60); await touch('touchEnd', []);
    await until(() => inputLog().slice(n0).some(e => e.t === 'button' && e.b === 0 && !e.down), 5000, 'tap click');
    const mv = inputLog().slice(n0).find(e => e.t === 'move'); assert.ok(Math.abs(mv.x - 640) <= 3 && Math.abs(mv.y - 360) <= 3, 'tap position ' + JSON.stringify(mv));
    // long press -> right click
    const n1 = inputLog().length;
    await touch('touchStart', [at(0.25, 0.25)]); await sleep(800); await touch('touchEnd', []);
    await until(() => inputLog().slice(n1).some(e => e.t === 'button' && e.b === 1 && !e.down), 5000, 'long press right click');
    assert.ok(!inputLog().slice(n1).some(e => e.t === 'button' && e.b === 0), 'no left click after a long press');
    // two fingers moving up together -> wheel
    const n2 = inputLog().length;
    let y = 0.7; await touch('touchStart', [at(0.4, y), at(0.6, y)]);
    for (let i = 0; i < 8; i++) { y -= 0.04; await touch('touchMove', [at(0.4, y), at(0.6, y)]); await sleep(30); }
    await touch('touchEnd', []);
    await until(() => inputLog().slice(n2).some(e => e.t === 'wheel'), 5000, 'two-finger scroll');
    // on-screen keyboard: text input
    await c.evaluate('document.getElementById("bKeys").click(); true');
    await c.send('Input.insertText', { text: 'ok' });
    await until(() => inputLog().some(e => e.t === 'text' && e.text.includes('o')), 5000, 'typed text');
    // the Ctrl+Alt+Del button is there but disabled, with the reason
    assert.equal(await c.evaluate('document.getElementById("bCad").disabled'), true);
    // chat from the page reaches the PC
    await c.evaluate('document.getElementById("chatText").value="hi from edge"; document.getElementById("chatForm").requestSubmit(); true');
    await until(() => T('host').log.some(l => l.includes('hi from edge')), 5000, 'edge chat');
    // stop from the PC side: the page goes back to the connect form with the reason
    cmd('host', 'disconnect');
    await until(() => c.evaluate('window.__assist.state === "ended"'), 5000, 'page told');
    c.close();
  } finally { edge.kill(); await sleep(500); try { spawnSync('taskkill', ['/F', '/T', '/PID', String(edge.pid)], { windowsHide: true }); } catch { } }
});

await step('direct connection (v1.3): on the same network the PCs talk directly - frames, input, chat and drawing no longer pass through the relay', async () => {
  cmd('host', 'test-direct on'); cmd('viewer', 'test-direct on');
  try {
    sess = await newHostSession();
    await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
    cmd('host', 'test-consent allow control');
    await until(() => T('viewer').frames > 0, 10000, 'frames');
    await until(() => T('host').direct === true && T('viewer').viewerDirect === true, 20000, 'both sides direct');
    await sleep(500);
    const relayedSealed = () => forwarded.filter(f => f.data[0] === P.T.SEALED).length;
    const before = relayedSealed(), f0 = T('viewer').frames, n0 = inputLog().length;
    cmd('viewer', 'test-view-monitor 1'); await until(() => T('viewer').monitor === 1 && T('viewer').frames > f0, 8000, 'new frames over the direct link');
    cmd('viewer', 'test-view-move 32768 32768'); await until(() => inputLog().slice(n0).some(e => e.t === 'move'), 5000, 'input over the direct link');
    cmd('viewer', 'test-view-chat direct hello'); await until(() => T('host').log.some(l => l.includes('chat from helper: direct hello')), 5000, 'chat over the direct link');
    const s0 = T('host').drawStrokes; cmd('viewer', 'test-view-draw 100 100 30000 30000'); await until(() => T('host').drawStrokes > s0, 5000, 'drawing reached the host');
    assert.equal(relayedSealed(), before, 'nothing sealed went through the relay after the switch');
    cmd('viewer', 'test-view-monitor 0'); await until(() => T('viewer').monitor === 0, 5000, 'back to monitor 1');
    // ending from the helper's side over the direct link: the "bye" arrives before the link closes
    cmd('viewer', 'test-view-end');
    await until(() => T('host').host === 'Ended', 8000, 'host ended');
    assert.match(T('host').endReason, /helper ended/i);
  } finally { cmd('host', 'test-direct relay'); cmd('viewer', 'test-direct relay'); }
});

// ---- v1.4
await step('direct connection drop (v1.4): the session falls back to the relay and keeps going (picture, input, chat)', async () => {
  cmd('host', 'test-direct on'); cmd('viewer', 'test-direct on');
  try {
    sess = await newHostSession();
    await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
    cmd('host', 'test-consent allow control');
    await until(() => T('viewer').frames > 0, 10000, 'frames');
    await until(() => T('host').direct === true && T('viewer').viewerDirect === true, 20000, 'both sides direct');
    const relayedSealed = () => forwarded.filter(f => f.data[0] === P.T.SEALED).length;
    const before = relayedSealed();
    cmd('viewer', 'test-view-cut-direct');
    await until(() => T('host').direct === false && T('viewer').viewerDirect === false, 10000, 'both sides back on the relay');
    assert.equal(T('host').host, 'Connected'); assert.equal(T('viewer').viewer, 'Connected');
    const f0 = T('viewer').frames, n0 = inputLog().length;
    cmd('viewer', 'test-view-monitor 1'); await until(() => T('viewer').monitor === 1 && T('viewer').frames > f0, 8000, 'frames over the relay after the drop');
    cmd('viewer', 'test-view-move 32768 32768'); await until(() => inputLog().slice(n0).some(e => e.t === 'move'), 5000, 'input over the relay after the drop');
    cmd('viewer', 'test-view-chat after the drop'); await until(() => T('host').log.some(l => l.includes('chat from helper: after the drop')), 5000, 'chat after the drop');
    cmd('host', 'test-host-chat host after the drop'); await until(() => T('viewer').log.some(l => l.includes('chat from host: host after the drop')), 5000, 'chat back after the drop');
    assert.ok(relayedSealed() > before, 'sealed messages go through the relay again');
    assert.ok(T('host').log.some(l => /back on the relay/.test(l)) || T('viewer').log.some(l => /back on the relay/.test(l)), 'the drop is in the details');
    cmd('viewer', 'test-view-monitor 0'); await until(() => T('viewer').monitor === 0, 5000, 'back to monitor 1');
    cmd('viewer', 'test-view-end');
    await until(() => T('host').host === 'Ended', 8000, 'host ended');
    assert.match(T('host').endReason, /helper ended/i);
  } finally { cmd('host', 'test-direct relay'); cmd('viewer', 'test-direct relay'); }
});

await step('session sound (v1.4): off on both sides by default; flows only when the helper asks AND the PC shares it; the bar says "Sound shared"', async () => {
  sess = await newHostSession();
  await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
  cmd('host', 'test-consent allow');
  await until(() => T('viewer').frames > 0, 10000, 'frames');
  assert.equal(T('host').soundShared, false); assert.equal(T('viewer').viewerSound, false); assert.ok(!/Sound shared/.test(T('host').barText));
  assert.equal(T('viewer').hostMac, null, 'a PC without unattended access never tells its network adapter');
  cmd('viewer', 'test-view-sound on');
  await until(() => T('host').log.some(l => l.includes('would like to hear')), 5000, 'the PC is asked');
  await sleep(1000);
  assert.equal(T('host').soundFlowing, false); assert.equal(T('viewer').audioPackets, 0, 'nothing before the PC shares it');
  cmd('host', 'test-host-sound on');
  await until(() => /Sound shared/.test(T('host').barText) && T('viewer').hostSharesSound === true, 5000, 'bar says Sound shared');
  await until(() => T('viewer').audioSamples >= 16000, 8000, 'a second of sound at the helper');
  const v = T('viewer'); assert.ok(Math.abs(v.audioHz - 440) < 25, 'the test tone arrives as 440 Hz: ' + v.audioHz);
  cmd('viewer', 'test-view-sound off');
  await until(() => T('host').soundFlowing === false, 5000, 'stops when the helper turns it off');
  const p0 = T('viewer').audioPackets; await sleep(800); assert.equal(T('viewer').audioPackets, p0);
  cmd('viewer', 'test-view-sound on'); await until(() => T('host').soundFlowing === true, 5000, 'flows again');
  cmd('host', 'test-host-sound off');
  await until(() => T('host').soundFlowing === false && !/Sound shared/.test(T('host').barText) && T('viewer').hostSharesSound === false, 5000, 'Stop sound on the bar');
  const all = Buffer.concat(forwarded.map(f => f.data)); assert.ok(forwarded.every(f => f.data[0] === P.T.SEALED || f.data.length <= 83), 'sound only ever crossed the relay sealed');
  cmd('viewer', 'test-view-end'); await until(() => T('host').host === 'Ended', 5000, 'ended');
});

await step('Wake-on-LAN (v1.4): one of Your PCs tells its owner its network adapter (unattended only); Wake sends the magic packet on ports 7/9, then waits for the online dot', async () => {
  const r = okJson(cmd('host', 'test-unattended-on Correct-Horse-42!'));
  try {
    await until(() => T('host').host === 'Waiting' && T('host').unattendedHost, 10000, 'unattended waiting');
    assert.equal(cmd('viewer', 'test-savepc ' + r.code + ' Office PC'), 'ok');
    assert.equal(T('viewer').pcs.find(p => p.code === r.code).mac, null);
    assert.match(cmd('viewer', 'test-wake ' + r.code), /^error no MAC/);
    await connectViewer(r.code, 'Correct-Horse-42!', 'Me');
    await until(() => T('viewer').frames > 0, 10000, 'unattended frames');
    assert.equal(T('viewer').hostSharesSound, false, 'sound is off in unattended sessions unless turned on in Settings');
    await until(() => (T('viewer').pcs.find(p => p.code === r.code) || {}).mac === '02005E102030', 5000, 'the MAC is learned');
    const settings = fs.readFileSync(path.join(dirs.viewer, 'settings.json'), 'utf8'); assert.ok(settings.includes('02005E102030'));
    wolPackets.length = 0;
    assert.equal(cmd('viewer', 'test-wake ' + r.code), 'ok');
    await until(() => wolPackets.length >= 1, 5000, 'magic packet');
    const pk = wolPackets[0]; const mac = Buffer.from('02005E102030', 'hex');
    assert.equal(pk.length, 102); assert.ok(pk.subarray(0, 6).every(b => b === 0xff));
    for (let i = 0; i < 16; i++) assert.ok(pk.subarray(6 + i * 6, 12 + i * 6).equals(mac), 'repeat ' + i);
    await until(() => T('viewer').wake === 'online', 10000, 'waits for the online dot');
    cmd('host', 'disconnect');
    await until(() => T('viewer').viewer === 'Ended', 5000, 'viewer disconnected');
    await until(() => T('host').host === 'Waiting', 8000, 'unattended keeps waiting');
  } finally { cmd('host', 'test-unattended-off'); }
  await until(() => status('host').unattended === false, 5000, 'unattended off');
});

await step('Hallow Keys (v1.5): one of Your PCs gets its password from a Keys item (a stand-in hk.exe, never the real one); each exit code gives its message; the password is never stored or logged', async () => {
  const PW = 'Keys-Test-Pass-77!';
  fs.writeFileSync(HK_ITEMS, 'Office PC (Assist)\t' + PW + '\n');
  const r = okJson(cmd('host', 'test-unattended-on ' + PW));
  try {
    await until(() => T('host').host === 'Waiting' && T('host').unattendedHost, 10000, 'unattended waiting');
    assert.equal(cmd('viewer', 'test-savepc ' + r.code + ' Keys PC'), 'ok');
    assert.match(cmd('viewer', 'test-connect-saved ' + r.code), /^error no password source/, 'no remembered key, no Keys item: type it');
    assert.equal(cmd('viewer', 'test-pc-keys ' + r.code + ' Office PC (Assist)'), 'ok');
    assert.equal(T('viewer').pcs.find(p => p.code === r.code).keysItem, 'Office PC (Assist)');
    fs.writeFileSync(HK_LOG, '');
    assert.equal(cmd('viewer', 'test-connect-saved ' + r.code), 'ok');
    await until(() => { const v = T('viewer'); return v.fromKeys && v.viewer === 'Connected' && v.frames > 0; }, 15000, 'connected with the password from Keys');
    assert.equal(T('viewer').keys, 'ok');
    assert.equal(fs.readFileSync(HK_LOG, 'utf8').trim(), '--get | Office PC (Assist) | --field | password');
    for (const f of ['settings.json', 'status.json', 'history.json']) { const p = path.join(dirs.viewer, f); if (fs.existsSync(p)) assert.ok(!fs.readFileSync(p, 'utf8').includes(PW), 'not in ' + f); }
    assert.ok(!JSON.stringify(status('viewer')).includes(PW), 'not in the log or status');
    cmd('host', 'disconnect'); await until(() => T('viewer').viewer === 'Ended', 5000, 'ended');
    await until(() => T('host').host === 'Waiting', 8000, 'waiting again');
    const msgs = { 1: "didn't understand", 2: 'Start Hallow Keys and try again', 3: 'Unlock Hallow Keys and try again', 4: 'has no item called', 5: 'Hallow Keys said no', 6: 'Turn on the command line in Keys > Settings > Developer tools', 7: "couldn't give the password" };
    for (const [c, text] of Object.entries(msgs)) {
      assert.equal(cmd('viewer', `test-pc-keys ${r.code} code-${c}`), 'ok');
      assert.equal(cmd('viewer', 'test-connect-saved ' + r.code), 'ok');
      await until(() => (T('viewer').keys || '').startsWith('code ' + c + ':'), 8000, 'exit ' + c);
      assert.ok(T('viewer').keys.includes(text), T('viewer').keys);
      assert.equal(T('viewer').viewer, 'Ended', 'no session without the password');
    }
    cmd('viewer', `test-pc-keys ${r.code} hang`); cmd('viewer', 'test-connect-saved ' + r.code);
    await until(() => (T('viewer').keys || '').startsWith('code -2:'), 12000, 'no answer: stopped after the timeout');
    // a wrong password in Keys: the connection fails and the message points at the item
    fs.writeFileSync(HK_ITEMS, 'Office PC (Assist)\tnot-the-right-one-1\n');
    cmd('viewer', `test-pc-keys ${r.code} Office PC (Assist)`); cmd('viewer', 'test-connect-saved ' + r.code);
    await until(() => T('viewer').log.some(l => l.includes('The password Hallow Keys gave')), 12000, 'wrong password from Keys');
  } finally { cmd('host', 'test-unattended-off'); }
  await until(() => status('host').unattended === false, 5000, 'unattended off');
});

const vids = path.join(dirs.viewer, 'Videos');
const recFiles = () => fs.existsSync(vids) ? fs.readdirSync(vids).filter(f => !f.includes('.part.')) : [];
await step('session recording (v1.5): off by default; starts only after the PC confirms "Recording" on its red bar (before the first recorded frame); picture and sound in the file; Stop recording on the bar stops it', async () => {
  sess = await newHostSession();
  await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
  cmd('host', 'test-consent allow control'); await until(() => T('viewer').frames > 0, 10000, 'frames');
  assert.equal(T('viewer').rec, 'Off'); assert.doesNotMatch(T('host').barText, /Recording/); assert.equal(recFiles().length, 0);
  cmd('viewer', 'test-view-sound on'); cmd('host', 'test-host-sound on');
  await until(() => T('viewer').audioPackets > 10, 8000, 'sound flowing');
  assert.equal(cmd('viewer', 'test-view-rec on'), 'ok');
  await until(() => T('viewer').rec === 'On', 5000, 'recording');
  const h = T('host'); assert.match(h.barText, /Recording/); assert.equal(h.recNotice, true);
  await until(() => T('viewer').recFrames >= 15, 10000, 'frames recorded');
  const v = T('viewer');
  assert.ok(h.recNoticeAt > 0 && v.recFirstFrameAt > 0 && h.recNoticeAt <= v.recFirstFrameAt, `the notice (${h.recNoticeAt}) came before the first recorded frame (${v.recFirstFrameAt})`);
  await sleep(5000); assert.equal(T('viewer').rec, 'On', 'kept going while the PC keeps confirming');
  cmd('viewer', 'test-view-rec off');
  await until(() => T('viewer').recPath && fs.existsSync(T('viewer').recPath), 20000, 'the file');
  await until(() => !/Recording/.test(T('host').barText), 5000, 'the notice goes away');
  const file = T('viewer').recPath, kind = process.env.HALLOW_ASSIST_FFMPEG ? 'mp4' : 'avi';
  assert.ok(file.startsWith(vids) && file.endsWith('.' + kind) && path.basename(file).includes(sess.code.slice(0, 3)), file); assert.equal(T('viewer').recKind, kind);
  const buf = fs.readFileSync(file);
  if (kind === 'avi') {
    assert.equal(buf.toString('ascii', 0, 4), 'RIFF'); assert.equal(buf.toString('ascii', 8, 12), 'AVI '); assert.equal(buf.readUInt32LE(4), buf.length - 8);
    assert.ok(buf.readUInt32LE(48) >= 50, 'about 10 frames a second: ' + buf.readUInt32LE(48));
    const first = buf.indexOf('00dc', 4108); assert.ok(first > 0 && buf[first + 8] === 0xff && buf[first + 9] === 0xd8, 'the first frame is a JPEG');
    assert.ok(buf.indexOf('auds') > 0 && buf.indexOf('01wb', 4108) > 0, 'their sound is in it');
  } else assert.equal(buf.toString('ascii', 4, 8), 'ftyp');
  fs.writeFileSync(path.join(WORK, 'recording.txt'), `${path.basename(file)} ${buf.length} bytes\n`);
  cmd('viewer', 'test-view-sound off'); cmd('host', 'test-host-sound off');
  // the person helped presses Stop recording on the red bar
  const before = recFiles().length;
  cmd('viewer', 'test-view-rec on'); await until(() => T('viewer').rec === 'On', 5000, 'recording again');
  await until(() => T('viewer').recFrames > 3, 5000, 'a few frames');
  cmd('host', 'test-host-rec-stop');
  await until(() => T('viewer').rec === 'Off', 5000, 'stopped by the PC');
  await until(() => T('viewer').log.some(l => l.includes('They stopped the recording')) && recFiles().length === before + 1, 20000, 'the helper is told and the file is kept');
  assert.doesNotMatch(T('host').barText, /Recording/);
});

await step("session recording (v1.5): never starts when the notice can't be shown on the PC, stops when the PC stops confirming it, and stops when the session ends", async () => {
  const n0 = recFiles().length;
  cmd('host', 'test-rec-noshow on');
  cmd('viewer', 'test-view-rec on');
  await until(() => T('viewer').rec === 'Off' && T('viewer').log.some(l => l.includes("didn't confirm")), 10000, 'gives up without a confirmation');
  assert.equal(T('host').recNotice, false); await until(() => T('host').recording === false, 5000, 'the request is withdrawn'); await sleep(300); assert.equal(recFiles().length, n0, 'no file');
  cmd('host', 'test-rec-noshow off');
  cmd('viewer', 'test-view-rec on'); await until(() => T('viewer').rec === 'On', 5000, 'recording');
  await until(() => T('viewer').recFrames > 5, 5000, 'frames');
  cmd('host', 'test-rec-noshow on');   // the bar can't say Recording any more: the confirmations stop
  await until(() => T('viewer').rec === 'Off', 10000, 'stops without confirmations');
  assert.ok(T('viewer').log.some(l => l.includes('stopped confirming')));
  await until(() => recFiles().length === n0 + 1, 20000, 'what was recorded while the notice showed is kept');
  cmd('host', 'test-rec-noshow off');
  cmd('viewer', 'test-view-rec on'); await until(() => T('viewer').rec === 'On', 5000, 'recording once more');
  await until(() => T('viewer').recFrames > 3, 5000, 'frames');
  cmd('host', 'disconnect');
  await until(() => T('viewer').viewer === 'Ended' && T('viewer').rec === 'Off', 8000, 'the session ended, so did the recording');
  await until(() => recFiles().length === n0 + 2, 20000, 'the file is finished');
  assert.ok(T('viewer').log.some(l => l.includes('recording saved')));
});

await step('closing the app (host) ends a connected session; the helper is told', async () => {
  sess = await newHostSession();
  await connectViewer(sess.code, sess.password); await until(() => T('host').consentPending, 10000, 'consent');
  cmd('host', 'test-consent allow'); await until(() => T('viewer').frames > 0, 10000, 'frames');
  cmd('host', 'test-close');
  await until(() => procs.hostExit !== undefined, 8000, 'host app closed');
  await until(() => T('viewer').viewer === 'Ended', 5000, 'viewer told');
});

cmd('viewer', 'quit');
await sleep(800);
for (const p of Object.values(procs)) { try { if (p && p.kill) p.kill(); } catch { } }
await relay.close();
try { wol.close(); } catch { }
console.log(`\n${passed} passed, ${failed} failed`);
fs.writeFileSync(path.join(WORK, 'integration-results.txt'), results.join('\n') + `\n${passed} passed, ${failed} failed\n`);
process.exit(failed ? 1 : 0);

// Relay tests: codes, pairing, expiry, guess rate limits, one viewer, kick, forwarding, idle timeout, health, static files.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRelay, persistentCode } from '../relay.js';
import { connect } from '../ws.js';

const start = async (opts = {}) => { const r = createRelay({ log: false, ...opts }); const port = await r.listen(0, '127.0.0.1'); return { r, port, url: `ws://127.0.0.1:${port}/ws` }; };
// a test client that collects messages
async function client(url) {
  const ws = await connect(url);
  const q = []; const w = [];
  ws.on('message', (d, bin) => { const m = bin ? d : JSON.parse(d); if (w.length) w.shift()(m); else q.push(m); });
  ws.closedP = new Promise(r => ws.on('close', (code, reason) => { ws.closeCode = code; r(code); while (w.length) w.shift()(null); }));
  ws.next = (ms = 2000) => new Promise(r => { if (q.length) return r(q.shift()); w.push(r); setTimeout(() => r('timeout'), ms); });
  ws.json = o => ws.send(JSON.stringify(o));
  return ws;
}
const sleep = ms => new Promise(r => setTimeout(r, ms));

test('host gets a 9-digit code; viewer joins; both are told; binary frames pass both ways unchanged', async () => {
  const { r, url } = await start();
  const h = await client(url); h.json({ t: 'host' });
  const c = await h.next(); assert.equal(c.t, 'code'); assert.match(c.code, /^[1-9]\d{8}$/); assert.equal(c.ttl, 600);
  const v = await client(url); v.json({ t: 'join', code: c.code.slice(0, 3) + ' ' + c.code.slice(3) });
  assert.equal((await v.next()).t, 'paired'); assert.equal((await h.next()).t, 'paired');
  v.send(Buffer.from([1, 2, 3])); assert.deepEqual([...(await h.next())], [1, 2, 3]);
  h.send(Buffer.from([9, 8])); assert.deepEqual([...(await v.next())], [9, 8]);
  h.close(); v.close(); await r.close();
});

test('codes are random and unique', async () => {
  const { r, url } = await start();
  const codes = new Set();
  for (let i = 0; i < 15; i++) { const h = await client(url); h.json({ t: 'host' }); codes.add((await h.next()).code); }
  assert.equal(codes.size, 15);
  await r.close();
});

test('an unused code expires (TTL) and the host is told', async () => {
  const { r, url } = await start({ codeTtlMs: 300 });
  const h = await client(url); h.json({ t: 'host' });
  const c = await h.next();
  assert.equal((await h.next(1000)).t, 'expired');
  assert.equal(await h.closedP, 4000);
  const v = await client(url); v.json({ t: 'join', code: c.code });
  assert.equal((await v.next()).reason, 'not-found');
  await r.close();
});

test('a paired session does not expire', async () => {
  const { r, url } = await start({ codeTtlMs: 200 });
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v = await client(url); v.json({ t: 'join', code: c.code }); await v.next(); await h.next();
  await sleep(400);
  v.send(Buffer.from([7])); assert.deepEqual([...(await h.next())], [7]);
  await r.close();
});

test('wrong codes are rate-limited per address', async () => {
  const { r, url } = await start({ guessLimit: 5 });
  for (let i = 0; i < 5; i++) { const v = await client(url); v.json({ t: 'join', code: '123456789' }); assert.equal((await v.next()).reason, 'not-found'); }
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v = await client(url); v.json({ t: 'join', code: c.code });   // even the right code is refused now
  const m = await v.next(); assert.equal(m.reason, 'rate-limited'); assert.ok(m.retryAfter > 0 && m.retryAfter <= 600);
  assert.equal(await v.closedP, 4029);
  await r.close();
});

test('rate limit uses CF-Connecting-IP only when trusted and from the tunnel', async () => {
  const { r, port } = await start({ guessLimit: 2, trustProxy: 'cloudflare' });
  const url = `ws://127.0.0.1:${port}/ws`;
  const as = async ip => { const ws = await connect(url, { headers: `CF-Connecting-IP: ${ip}\r\n` }); const p = new Promise(res => ws.on('message', d => res(JSON.parse(d)))); ws.send(JSON.stringify({ t: 'join', code: '111111111' })); return p; };
  assert.equal((await as('203.0.113.1')).reason, 'not-found');
  assert.equal((await as('203.0.113.1')).reason, 'not-found');
  assert.equal((await as('203.0.113.1')).reason, 'rate-limited');
  assert.equal((await as('203.0.113.2')).reason, 'not-found');   // another visitor is not blocked
  await r.close();
});

test('one viewer per session: a second viewer is told busy', async () => {
  const { r, url } = await start();
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v1 = await client(url); v1.json({ t: 'join', code: c.code }); assert.equal((await v1.next()).t, 'paired');
  const v2 = await client(url); v2.json({ t: 'join', code: c.code }); assert.equal((await v2.next()).reason, 'busy');
  await r.close();
});

test('host can kick the viewer (wrong password); the code opens again', async () => {
  const { r, url } = await start();
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v1 = await client(url); v1.json({ t: 'join', code: c.code }); await v1.next(); await h.next();
  h.json({ t: 'kick' });
  assert.equal((await v1.next()).t, 'kicked'); assert.equal(await v1.closedP, 4001);
  const v2 = await client(url); v2.json({ t: 'join', code: c.code }); assert.equal((await v2.next()).t, 'paired');
  await r.close();
});

test('disconnect from either side: viewer leaving tells the host; host leaving closes the viewer and the code', async () => {
  const { r, url } = await start();
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v = await client(url); v.json({ t: 'join', code: c.code }); await v.next(); await h.next();
  v.close();
  assert.equal((await h.next()).t, 'peer-left');
  const v2 = await client(url); v2.json({ t: 'join', code: c.code }); assert.equal((await v2.next()).t, 'paired'); await h.next();
  h.close();
  assert.equal((await v2.next()).t, 'peer-left'); assert.equal(await v2.closedP, 4005);
  assert.equal(r.sessions.size, 0);
  await r.close();
});

test('unattended host: the code comes from its secret and the same secret takes the code back', async () => {
  const { r, url } = await start();
  const secret = 'ab'.repeat(32);
  const h1 = await client(url); h1.json({ t: 'host', persist: secret });
  const c1 = await h1.next(); assert.equal(c1.code, persistentCode(secret)); assert.equal(c1.ttl, 0);
  const h2 = await client(url); h2.json({ t: 'host', persist: secret });
  assert.equal((await h2.next()).code, c1.code);
  assert.equal(await h1.closedP, 4004);
  const bad = await client(url); bad.json({ t: 'host', persist: 'not hex' }); assert.equal(await bad.closedP, 1008);
  await r.close();
});

test('binary before pairing, junk hello and silence are refused', async () => {
  const { r, url } = await start({ idleMs: 600 });
  const a = await client(url); a.send(Buffer.from([1])); assert.equal(await a.closedP, 1008);
  const b = await client(url); b.send('{"t":"nope"}'); assert.equal(await b.closedP, 1008);
  // idle: a paired pair that stops answering pings is dropped (the test client answers pings, so check a raw idle host)
  await r.close();
});

test('idle connections are dropped', async () => {
  const { r, url } = await start({ idleMs: 300 });
  const h = await client(url); h.json({ t: 'host' }); await h.next();
  h.socket.pause();   // stops reading, so pings go unanswered
  const t0 = Date.now();
  while (r.sessions.size > 0 && Date.now() - t0 < 3000) await sleep(50);
  assert.equal(r.sessions.size, 0);
  await r.close();
});

test('oversized relay message closes with 1009', async () => {
  const { r, url } = await start({ maxMessage: 1000 });
  const h = await client(url); h.json({ t: 'host' }); const c = await h.next();
  const v = await client(url); v.json({ t: 'join', code: c.code }); await v.next(); await h.next();
  v.send(Buffer.alloc(5000));
  assert.equal(await v.closedP, 1009);
  await r.close();
});

test('health endpoint, viewer page with security headers, 404 for anything else', async () => {
  const { r, port } = await start({ publicUrl: 'https://assist.example.com' });
  const base = `http://127.0.0.1:${port}`;
  const hz = await fetch(base + '/healthz'); assert.equal(hz.status, 200);
  const j = await hz.json(); assert.equal(j.ok, true); assert.equal(j.sessions, 0);
  const page = await fetch(base + '/'); assert.equal(page.status, 200);
  assert.match(page.headers.get('content-security-policy'), /default-src 'self'/);
  assert.equal(page.headers.get('x-frame-options'), 'DENY');
  assert.match(await page.text(), /Hallow Assist/);
  for (const p of ['/proto.js', '/viewer.js', '/keys.js', '/viewer.css']) assert.equal((await fetch(base + p)).status, 200, p);
  for (const p of ['/../relay.js', '/relay.js', '/package.json', '/%2e%2e/relay.js']) assert.equal((await fetch(base + p)).status, 404, p);
  assert.equal((await (await fetch(base + '/config.json')).json()).publicUrl, 'https://assist.example.com');
  await r.close();
});

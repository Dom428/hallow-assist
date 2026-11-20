// End-to-end crypto through a real relay (both ends in JS here; Windows <-> browser interop is in interop.test.mjs):
// key confirmation, wrong password, tampered and replayed frames, and that the relay only ever sees ciphertext.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createRelay } from '../relay.js';
import { connect } from '../ws.js';
import * as P from '../public/proto.js';

// Node's own WebSocket client stands in for the browser's
const WS = globalThis.WebSocket;

// a JS "host" end (what the Windows app does), enough for the tests
async function jsHost(url, password, { onPlain } = {}) {
  const ws = await connect(url);
  const st = { ws, events: [], chan: null };
  let code, pwKey, kp, keys;
  const ready = new Promise(res => {
    ws.on('message', async (d, bin) => {
      if (!bin) {
        const m = JSON.parse(d);
        st.events.push(m.t);
        if (m.t === 'code') { code = m.code; st.code = code; pwKey = await P.passwordKey(password, code); res(); }
        return;
      }
      const b = new Uint8Array(d);
      if (b[0] === P.T.V_HELLO) {
        kp = await P.newKeyPair(); const nonce = globalThis.crypto.getRandomValues(new Uint8Array(16));
        const viewerPub = b.slice(2);
        keys = await P.deriveSession({ priv: kp.priv, peerPub: viewerPub, viewerPub, hostPub: kp.pub, nonce, code, pwKey });
        ws.send(Buffer.from(P.cat(new Uint8Array([P.T.H_HELLO, P.VER]), kp.pub, nonce)));
      } else if (b[0] === P.T.V_CONFIRM) {
        if (!P.equalBytes(b.subarray(1), keys.confirmV)) { st.events.push('wrong-password'); ws.send(Buffer.from([P.T.H_REJECT])); ws.send(JSON.stringify({ t: 'kick' })); return; }
        ws.send(Buffer.from(P.cat(new Uint8Array([P.T.H_CONFIRM]), keys.confirmH)));
        st.chan = await new P.Channel(keys, 'host').init(); st.safety = keys.safety;
      } else if (b[0] === P.T.SEALED) {
        try { const p = await st.chan.open(b); onPlain?.(p); } catch (e) { st.events.push('rejected: ' + e.message); }
      }
    });
  });
  ws.send(JSON.stringify({ t: 'host' }));
  await ready;
  st.send = async plain => ws.send(Buffer.from(await st.chan.seal(plain)));
  return st;
}
const until = async (f, ms = 5000) => { const t0 = Date.now(); while (!f()) { if (Date.now() - t0 > ms) throw new Error('timed out'); await new Promise(r => setTimeout(r, 20)); } };

test('handshake: both ends get the same safety code; messages arrive; the relay sees only ciphertext', async () => {
  const seen = [];
  const r = createRelay({ log: false, onForward: (role, data) => seen.push({ role, data: Buffer.from(data) }) });
  const port = await r.listen(0, '127.0.0.1'); const url = `ws://127.0.0.1:${port}/ws`;
  const got = [];
  const host = await jsHost(url, 'K7MX2P', { onPlain: p => got.push(P.parseJson(p)) });
  const link = new P.ViewerLink({ url, code: host.code, password: 'k7mx2p', name: 'Test helper', client: 'test', WebSocketImpl: WS });
  let safety = null, ready = false, ended = null;
  const back = [];
  link.onSafety = s => safety = s; link.onReady = () => ready = true; link.onEnd = e => ended = e; link.onMessage = p => back.push(P.parseJson(p));
  await link.start();
  await until(() => ready && got.length >= 1);
  assert.equal(got[0].t, 'hello'); assert.equal(got[0].name, 'Test helper');
  assert.equal(safety, host.safety); assert.match(safety, /^\d{4}$/);
  const secret = 'SECRET-CHAT-' + 'x'.repeat(40);
  await link.send(P.jsonMsg({ t: 'chat', text: secret }));
  await host.send(P.jsonMsg({ t: 'chat', text: secret + '-back' }));
  await until(() => got.length >= 2 && back.length >= 1);
  assert.equal(got[1].text, secret); assert.equal(back[0].text, secret + '-back');
  // what the relay passed along: the handshake (public keys, nonce, MACs) and sealed frames - never the text
  const all = Buffer.concat(seen.map(s => s.data));
  assert.equal(all.indexOf('SECRET-CHAT'), -1); assert.equal(all.indexOf('Test helper'), -1); assert.equal(all.indexOf('"t"'), -1);
  const sealed = seen.filter(s => s.data[0] === P.T.SEALED);
  assert.ok(sealed.length >= 3);
  // ciphertext looks random: byte histogram of the sealed bodies is flat-ish
  const body = Buffer.concat(sealed.map(s => s.data.subarray(9))); const hist = new Array(256).fill(0); for (const x of body) hist[x]++;
  assert.ok(Math.max(...hist) < body.length / 256 * 4 + 8);
  assert.equal(ended, null);
  link.end('done'); host.ws.close(); await r.close();
});

test('wrong password fails key confirmation; nothing sealed is ever sent', async () => {
  const seen = [];
  const r = createRelay({ log: false, onForward: (role, data) => seen.push(Buffer.from(data)) });
  const port = await r.listen(0, '127.0.0.1'); const url = `ws://127.0.0.1:${port}/ws`;
  const host = await jsHost(url, 'K7MX2P');
  const link = new P.ViewerLink({ url, code: host.code, password: 'AAAAAA', name: 'x', WebSocketImpl: WS });
  let ended = null; link.onEnd = e => ended = e;
  await link.start();
  await until(() => ended !== null);
  assert.equal(ended, 'Wrong password.');
  assert.ok(host.events.includes('wrong-password'));
  assert.equal(host.chan, null);
  assert.equal(seen.filter(b => b[0] === P.T.SEALED).length, 0);
  host.ws.close(); await r.close();
});

test('tampered, replayed and reordered sealed messages are rejected', async () => {
  const kpV = await P.newKeyPair(), kpH = await P.newKeyPair(); const nonce = new Uint8Array(16).fill(7);
  const pwKey = await P.passwordKey('ABCDEF', '123456789');
  const kv = await P.deriveSession({ priv: kpV.priv, peerPub: kpH.pub, viewerPub: kpV.pub, hostPub: kpH.pub, nonce, code: '123456789', pwKey });
  const kh = await P.deriveSession({ priv: kpH.priv, peerPub: kpV.pub, viewerPub: kpV.pub, hostPub: kpH.pub, nonce, code: '123456789', pwKey });
  assert.ok(P.equalBytes(kv.kV2H, kh.kV2H)); assert.equal(kv.safety, kh.safety);
  const v = await new P.Channel(kv, 'viewer').init(), h = await new P.Channel(kh, 'host').init();
  const m1 = await v.seal(P.utf8('one')), m2 = await v.seal(P.utf8('two')), m3 = await v.seal(P.utf8('three'));
  // tampered body
  const bad = m1.slice(); bad[12] ^= 1;
  await assert.rejects(h.open(bad), /changed/);
  // tampered header (counter is authenticated as associated data)
  const h2 = await new P.Channel(kh, 'host').init();
  assert.equal(new TextDecoder().decode(await h2.open(m1)), 'one');
  await assert.rejects(h2.open(m1), /replayed/);
  await assert.rejects(h2.open(m3), /out of order/);
  assert.equal(new TextDecoder().decode(await h2.open(m2)), 'two');
  // a message sealed for the other direction doesn't open
  const back = await h.seal(P.utf8('back'));
  const v2 = await new P.Channel(kh, 'host').init();
  await assert.rejects(v2.open(back), /changed/);
  // different password -> different keys -> nothing opens
  const kOther = await P.deriveSession({ priv: kpH.priv, peerPub: kpV.pub, viewerPub: kpV.pub, hostPub: kpH.pub, nonce, code: '123456789', pwKey: await P.passwordKey('ABCDEG', '123456789') });
  assert.ok(!P.equalBytes(kOther.confirmV, kv.confirmV));
});

test('password normalisation: one-time passwords ignore case and spaces; long passwords are exact', () => {
  assert.equal(P.normPassword(' k7m x2p '), 'K7MX2P');
  assert.equal(P.normPassword('k7m-x2p'), 'K7MX2P');
  assert.equal(P.normPassword('My Long Password 9'), 'My Long Password 9');
  assert.equal(P.normCode('123 456-789'), '123456789');
});

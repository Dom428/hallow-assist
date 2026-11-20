// Hallow Assist end-to-end protocol (browser viewer side; the Windows app has the same in Crypto.cs / Proto.cs).
// Works in any modern browser and in Node 20+ (WebCrypto). The relay only ever sees the bytes this produces.
//
// Handshake (binary messages through the relay, after it has paired the two ends):
//   viewer -> host  0x01 | ver | viewerPub(65)            ECDH P-256, uncompressed point
//   host -> viewer  0x02 | ver | hostPub(65) | nonce(16)
//   viewer -> host  0x03 | HMAC(macV, "viewer" | th)        key confirmation: proves the viewer knows the password
//   host -> viewer  0x04 | HMAC(macH, "host" | th)          (or 0x05 = wrong password)
// Keys: pwKey = PBKDF2-SHA256(password, "hallow-assist/v1/pw/" + code, 100000)
//       th    = SHA256("hallow-assist/v1" | code | viewerPub | hostPub | nonce)
//       okm   = HKDF-SHA256(ikm = SHA256(ECDH x) | pwKey, salt = th, info = "hallow-assist/v1/keys", 160 bytes)
//             = kV2H | kH2V | macV | macH | safety
// After that every message is 0x10 | counter(8, BE) | AES-256-GCM(key, iv = dir(4) | counter(8), aad = first 9 bytes).
// Counters start at 1 and must go up by exactly one: a replayed, dropped or reordered message ends the session.
export const VER = 1;
export const ITER = 100000;
export const T = { V_HELLO: 1, H_HELLO: 2, V_CONFIRM: 3, H_CONFIRM: 4, H_REJECT: 5, SEALED: 0x10 };
export const M = { JSON: 1, FRAME: 2, INPUT: 3, FILE: 4 };
export const IN = { MOVE: 1, BUTTON: 2, WHEEL: 3, KEY: 4, TEXT: 5 };

const te = new TextEncoder(), td = new TextDecoder();
const subtle = () => globalThis.crypto.subtle;
export const utf8 = s => te.encode(s);
export const cat = (...parts) => { const n = parts.reduce((a, p) => a + p.length, 0); const o = new Uint8Array(n); let i = 0; for (const p of parts) { o.set(p, i); i += p.length; } return o; };
const sha256 = async b => new Uint8Array(await subtle().digest('SHA-256', b));

// One-time passwords are 6 letters/digits and case doesn't matter; longer (unattended) passwords are used as typed.
export function normPassword(pw) {
  const s = String(pw || '').trim();
  const compact = s.replace(/[\s-]/g, '');
  return /^[A-Za-z0-9]{6}$/.test(compact) ? compact.toUpperCase() : s;
}
export const normCode = c => String(c || '').replace(/\D/g, '');

export async function passwordKey(password, code) {
  const k = await subtle().importKey('raw', utf8(normPassword(password)), 'PBKDF2', false, ['deriveBits']);
  return new Uint8Array(await subtle().deriveBits({ name: 'PBKDF2', hash: 'SHA-256', salt: utf8('hallow-assist/v1/pw/' + normCode(code)), iterations: ITER }, k, 256));
}

export async function newKeyPair() {
  const kp = await subtle().generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  return { priv: kp.privateKey, pub: new Uint8Array(await subtle().exportKey('raw', kp.publicKey)) };
}

async function hkdf(ikm, salt, info, bytes) {
  const k = await subtle().importKey('raw', ikm, 'HKDF', false, ['deriveBits']);
  return new Uint8Array(await subtle().deriveBits({ name: 'HKDF', hash: 'SHA-256', salt, info }, k, bytes * 8));
}
async function hmac(key, data) {
  const k = await subtle().importKey('raw', key, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await subtle().sign('HMAC', k, data));
}
export function equalBytes(a, b) { if (a.length !== b.length) return false; let d = 0; for (let i = 0; i < a.length; i++) d |= a[i] ^ b[i]; return d === 0; }

// Both ends: the session keys from my private key, the other end's public key and the password key.
export async function deriveSession({ priv, peerPub, viewerPub, hostPub, nonce, code, pwKey }) {
  if (!(peerPub instanceof Uint8Array) || peerPub.length !== 65 || peerPub[0] !== 4) throw new Error('bad public key');
  const peer = await subtle().importKey('raw', peerPub, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  const z = new Uint8Array(await subtle().deriveBits({ name: 'ECDH', public: peer }, priv, 256));
  const zh = await sha256(z);
  const th = await sha256(cat(utf8('hallow-assist/v1'), utf8(normCode(code)), viewerPub, hostPub, nonce));
  const okm = await hkdf(cat(zh, pwKey), th, utf8('hallow-assist/v1/keys'), 160);
  const s = { th, kV2H: okm.slice(0, 32), kH2V: okm.slice(32, 64), macV: okm.slice(64, 96), macH: okm.slice(96, 128), safetyBytes: okm.slice(128, 160) };
  s.confirmV = await hmac(s.macV, cat(utf8('viewer'), th));
  s.confirmH = await hmac(s.macH, cat(utf8('host'), th));
  const n = ((s.safetyBytes[0] << 24) >>> 0) + (s.safetyBytes[1] << 16) + (s.safetyBytes[2] << 8) + s.safetyBytes[3];
  s.safety = String(n % 10000).padStart(4, '0');
  return s;
}

// A sealed channel in one direction pair. role = 'viewer' or 'host'.
export class Channel {
  constructor(sess, role) { this.sess = sess; this.role = role; this.sendCtr = 0n; this.recvCtr = 0n; this.sendKey = null; this.recvKey = null; this.sendChain = Promise.resolve(); this.recvChain = Promise.resolve(); }
  async init() {
    const imp = k => subtle().importKey('raw', k, 'AES-GCM', false, ['encrypt', 'decrypt']);
    const v = this.role === 'viewer';
    this.sendKey = await imp(v ? this.sess.kV2H : this.sess.kH2V);
    this.recvKey = await imp(v ? this.sess.kH2V : this.sess.kV2H);
    this.sendDir = v ? 1 : 2; this.recvDir = v ? 2 : 1;
    return this;
  }
  static iv(dir, ctr) { const iv = new Uint8Array(12); new DataView(iv.buffer).setUint32(0, dir); new DataView(iv.buffer).setBigUint64(4, ctr); return iv; }
  // seal(plaintext) -> Promise<Uint8Array> (in call order: counters are taken now)
  seal(plain) {
    const ctr = ++this.sendCtr;
    const head = new Uint8Array(9); head[0] = T.SEALED; new DataView(head.buffer).setBigUint64(1, ctr);
    const p = this.sendChain.then(async () => cat(head, new Uint8Array(await subtle().encrypt({ name: 'AES-GCM', iv: Channel.iv(this.sendDir, ctr), additionalData: head, tagLength: 128 }, this.sendKey, plain))));
    this.sendChain = p.catch(() => {});
    return p;
  }
  // open(message) -> Promise<Uint8Array>; rejects on a tampered, replayed or out-of-order message
  open(msg) {
    const p = this.recvChain.then(async () => {
      if (msg.length < 9 + 16 || msg[0] !== T.SEALED) throw new Error('not a sealed message');
      const ctr = new DataView(msg.buffer, msg.byteOffset + 1, 8).getBigUint64(0);
      if (ctr !== this.recvCtr + 1n) throw new Error(ctr <= this.recvCtr ? 'replayed message' : 'message out of order');
      let plain;
      try { plain = new Uint8Array(await subtle().decrypt({ name: 'AES-GCM', iv: Channel.iv(this.recvDir, ctr), additionalData: msg.subarray(0, 9), tagLength: 128 }, this.recvKey, msg.subarray(9))); }
      catch { throw new Error('message was changed on the way'); }
      this.recvCtr = ctr;
      return plain;
    });
    this.recvChain = p.catch(() => {});
    return p;
  }
}

// ---- inner messages
export const jsonMsg = obj => cat(new Uint8Array([M.JSON]), utf8(JSON.stringify(obj)));
export const parseJson = b => JSON.parse(td.decode(b.subarray(1)));

export function input(kind, ...v) {
  const dv = (n) => { const b = new Uint8Array(n); return [b, new DataView(b.buffer)]; };
  if (kind === IN.MOVE) { const [b, d] = dv(6); b[0] = M.INPUT; b[1] = IN.MOVE; d.setUint16(2, v[0]); d.setUint16(4, v[1]); return b; }
  if (kind === IN.BUTTON) { const [b, d] = dv(8); b[0] = M.INPUT; b[1] = IN.BUTTON; b[2] = v[0]; b[3] = v[1] ? 1 : 0; d.setUint16(4, v[2]); d.setUint16(6, v[3]); return b; }
  if (kind === IN.WHEEL) { const [b, d] = dv(6); b[0] = M.INPUT; b[1] = IN.WHEEL; d.setInt16(2, v[0]); d.setInt16(4, v[1] || 0); return b; }
  if (kind === IN.KEY) { const [b, d] = dv(5); b[0] = M.INPUT; b[1] = IN.KEY; d.setUint16(2, v[0]); b[4] = v[1] ? 1 : 0; return b; }
  if (kind === IN.TEXT) return cat(new Uint8Array([M.INPUT, IN.TEXT]), utf8(String(v[0]).slice(0, 2000)));
  throw new Error('unknown input');
}

// FRAME: mon(1) srcW(2) srcH(2) encW(2) encH(2) seq(4) flags(1) count(2) then count x [x y w h (2 each) len(4) jpeg]
export function parseFrame(b) {
  const d = new DataView(b.buffer, b.byteOffset, b.byteLength);
  let o = 1;
  const f = { mon: b[o], srcW: d.getUint16(o + 1), srcH: d.getUint16(o + 3), encW: d.getUint16(o + 5), encH: d.getUint16(o + 7), seq: d.getUint32(o + 9), flags: b[o + 13], tiles: [] };
  const n = d.getUint16(o + 14); o += 16;
  for (let i = 0; i < n; i++) {
    const t = { x: d.getUint16(o), y: d.getUint16(o + 2), w: d.getUint16(o + 4), h: d.getUint16(o + 6) }; const len = d.getUint32(o + 8); o += 12;
    if (o + len > b.length) throw new Error('frame cut short');
    t.jpeg = b.subarray(o, o + len); o += len; f.tiles.push(t);
  }
  return f;
}
export function fileChunk(id, data) { const b = new Uint8Array(5 + data.length); b[0] = M.FILE; new DataView(b.buffer).setUint32(1, id); b.set(data, 5); return b; }
export function parseFileChunk(b) { return { id: new DataView(b.buffer, b.byteOffset, b.byteLength).getUint32(1), data: b.subarray(5) }; }

// ---- the viewer's side of a whole session over a WebSocket-like object (browser WebSocket or Node's)
// events: onState(text), onSafety(code), onReady(), onMessage(plainBytes), onEnd(reason)
export class ViewerLink {
  constructor({ url, code, password, name, client = 'browser', WebSocketImpl = globalThis.WebSocket }) {
    Object.assign(this, { url, code: normCode(code), password, name, client, WS: WebSocketImpl });
    this.onState = () => {}; this.onSafety = () => {}; this.onReady = () => {}; this.onMessage = () => {}; this.onEnd = () => {};
    this.ended = false; this.chan = null;
  }
  async start() {
    this.onState('Connecting to the relay...');
    const pwKeyP = passwordKey(this.password, this.code);
    const kpP = newKeyPair();
    const ws = this.ws = new this.WS(this.url); ws.binaryType = 'arraybuffer';
    let step = 'join', keys = null, nonce = null, hostPub = null;
    ws.onopen = () => { ws.send(JSON.stringify({ t: 'join', code: this.code })); this.ping = setInterval(() => { try { ws.send(JSON.stringify({ t: 'ping' })); } catch { } }, 20000); };
    ws.onclose = () => this.end(step === 'join' ? 'Couldn’t reach the relay.' : 'The connection closed.');
    ws.onerror = () => {};
    ws.onmessage = async ev => {
      try {
        if (typeof ev.data === 'string') {
          const m = JSON.parse(ev.data);
          if (m.t === 'error') return this.end(m.reason === 'not-found' ? 'That code isn’t open. Check the 9 digits, or ask them for a new code.' : m.reason === 'busy' ? 'Someone is already connected to that code.' : m.reason === 'rate-limited' ? 'Too many tries from here. Wait ' + Math.ceil((m.retryAfter || 600) / 60) + ' minutes.' : 'The relay said no (' + m.reason + ').');
          if (m.t === 'paired') { step = 'hello'; this.onState('Found them. Checking the password...'); const kp = await kpP; this.kp = kp; ws.send(cat(new Uint8Array([T.V_HELLO, VER]), kp.pub)); }
          if (m.t === 'peer-left') this.end('They closed the session.');
          if (m.t === 'kicked') this.end(this.rejected ? 'Wrong password.' : 'They ended the session.');
          return;
        }
        const b = new Uint8Array(ev.data);
        if (step === 'hello' && b[0] === T.H_HELLO && b.length === 2 + 65 + 16) {
          hostPub = b.slice(2, 67); nonce = b.slice(67, 83);
          keys = await deriveSession({ priv: this.kp.priv, peerPub: hostPub, viewerPub: this.kp.pub, hostPub, nonce, code: this.code, pwKey: await pwKeyP });
          step = 'confirm';
          ws.send(cat(new Uint8Array([T.V_CONFIRM]), keys.confirmV));
          return;
        }
        if (step === 'confirm' && b[0] === T.H_REJECT) { this.rejected = true; return this.end('Wrong password.'); }
        if (step === 'confirm' && b[0] === T.H_CONFIRM) {
          if (!equalBytes(b.subarray(1), keys.confirmH)) return this.end('Couldn’t verify the other PC - the connection may not be safe.');
          this.chan = await new Channel(keys, 'viewer').init();
          step = 'open'; this.safety = keys.safety; this.onSafety(keys.safety);
          await this.send(jsonMsg({ t: 'hello', name: String(this.name || 'Helper').slice(0, 40), client: this.client, version: '1.0' }));
          this.onReady();
          return;
        }
        if (step === 'open' && b[0] === T.SEALED) { const p = await this.chan.open(b); this.onMessage(p); return; }
        this.end('Unexpected message - the session was closed to be safe.');
      } catch (e) { this.end(String(e && e.message || e)); }
    };
  }
  async send(plain) { if (!this.chan || this.ended) return; const s = await this.chan.seal(plain); if (!this.ended) this.ws.send(s); }
  end(reason) {
    if (this.ended) return; this.ended = true; clearInterval(this.ping);
    try { this.ws.close(); } catch { }
    this.onEnd(reason);
  }
}

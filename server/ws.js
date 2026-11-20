// Hallow Assist relay - a small WebSocket implementation (RFC 6455), so the relay needs no npm packages.
// Server side (clients must mask their frames) and a tiny client side used by the tests.
// Handles fragmentation, ping/pong, the close handshake, UTF-8 checks for text and a maximum message size.
import crypto from 'node:crypto';
import { EventEmitter } from 'node:events';
import net from 'node:net';

const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
export const acceptKey = key => crypto.createHash('sha1').update(key + GUID).digest('base64');

const OP = { CONT: 0, TEXT: 1, BIN: 2, CLOSE: 8, PING: 9, PONG: 10 };
const utf8 = new TextDecoder('utf-8', { fatal: true });

// close codes a peer may send (RFC 6455 7.4)
const validCloseCode = c => (c >= 1000 && c <= 1003) || (c >= 1007 && c <= 1014 && c !== 1015) || (c >= 3000 && c <= 4999);

export class WebSocketConn extends EventEmitter {
  // opts.client: this end is the client (masks its frames, expects unmasked ones). opts.maxMessage: bytes.
  constructor(socket, opts = {}) {
    super();
    this.socket = socket;
    this.client = !!opts.client;
    this.maxMessage = opts.maxMessage || 4 * 1024 * 1024;
    this.buf = Buffer.alloc(0);
    this.parts = null; this.partsOp = 0; this.partsLen = 0;
    this.state = 'open';   // open -> closing -> closed
    this.closeTimer = null;
    this.lastSeen = Date.now();
    socket.setNoDelay?.(true);
    socket.on('data', d => this._data(d));
    socket.on('close', () => this._finish(1006, ''));
    socket.on('error', () => {});
    socket.on('drain', () => this.emit('drain'));
  }

  get bufferedAmount() { return this.socket.writableLength || 0; }
  get open() { return this.state === 'open'; }

  _data(d) {
    this.lastSeen = Date.now();
    this.buf = this.buf.length ? Buffer.concat([this.buf, d]) : d;
    while (this.state !== 'closed') {
      const b = this.buf;
      if (b.length < 2) return;
      const fin = (b[0] & 0x80) !== 0, rsv = b[0] & 0x70, op = b[0] & 0x0f;
      const masked = (b[1] & 0x80) !== 0;
      let len = b[1] & 0x7f, off = 2;
      if (rsv) return this._fail(1002, 'reserved bits set');
      if (masked === this.client) return this._fail(1002, this.client ? 'server frames must not be masked' : 'client frames must be masked');
      if (len === 126) { if (b.length < 4) return; len = b.readUInt16BE(2); off = 4; }
      else if (len === 127) {
        if (b.length < 10) return;
        const hi = b.readUInt32BE(2), lo = b.readUInt32BE(6);
        if (hi !== 0 || lo > 0x7fffffff) return this._fail(1009, 'message too big');
        len = lo; off = 10;
      }
      const control = op >= 8;
      if (control) {
        if (!fin) return this._fail(1002, 'fragmented control frame');
        if (len > 125) return this._fail(1002, 'control frame too long');
        if (op !== OP.CLOSE && op !== OP.PING && op !== OP.PONG) return this._fail(1002, 'unknown opcode');
      } else {
        if (op !== OP.CONT && op !== OP.TEXT && op !== OP.BIN) return this._fail(1002, 'unknown opcode');
        if (op === OP.CONT && !this.parts) return this._fail(1002, 'continuation without a start');
        if (op !== OP.CONT && this.parts) return this._fail(1002, 'new message inside a fragmented one');
        // refuse before buffering the payload
        if ((op === OP.CONT ? this.partsLen : 0) + len > this.maxMessage) return this._fail(1009, 'message too big');
      }
      const maskOff = off; if (masked) off += 4;
      if (b.length < off + len) return;
      let payload = b.subarray(off, off + len);
      if (masked) {
        const m = b.subarray(maskOff, maskOff + 4); const p = Buffer.allocUnsafe(len);
        for (let i = 0; i < len; i++) p[i] = payload[i] ^ m[i & 3];
        payload = p;
      } else payload = Buffer.from(payload);
      this.buf = b.subarray(off + len);
      if (control) { this._control(op, payload); continue; }
      if (op === OP.CONT) { this.parts.push(payload); this.partsLen += len; }
      else { this.parts = [payload]; this.partsOp = op; this.partsLen = len; }
      if (fin) {
        const whole = this.parts.length === 1 ? this.parts[0] : Buffer.concat(this.parts, this.partsLen);
        const kind = this.partsOp; this.parts = null; this.partsLen = 0;
        if (this.state !== 'open') continue;   // after our close frame: drop data
        if (kind === OP.TEXT) {
          let s; try { s = utf8.decode(whole); } catch { return this._fail(1007, 'invalid UTF-8'); }
          this.emit('message', s, false);
        } else this.emit('message', whole, true);
      }
    }
  }

  _control(op, payload) {
    if (op === OP.PING) { if (this.state === 'open') this._frame(OP.PONG, payload); return; }
    if (op === OP.PONG) { this.emit('pong', payload); return; }
    // close
    let code = 1005, reason = '';
    if (payload.length === 1) return this._fail(1002, 'bad close frame');
    if (payload.length >= 2) {
      code = payload.readUInt16BE(0);
      if (!validCloseCode(code)) return this._fail(1002, 'bad close code');
      try { reason = utf8.decode(payload.subarray(2)); } catch { return this._fail(1007, 'invalid UTF-8 in close reason'); }
    }
    if (this.state === 'open') { this.state = 'closing'; this._frame(OP.CLOSE, payload.length >= 2 ? payload.subarray(0, 2) : Buffer.alloc(0)); }
    this.peerClose = { code, reason };
    // the server closes the TCP connection first (RFC 6455 7.1.1); the client waits for it
    if (!this.client) this.socket.end();
    this._armKill();
  }

  _frame(op, data) {
    if (this.socket.destroyed) return false;
    const len = data.length;
    const head = len < 126 ? 2 : len < 65536 ? 4 : 10;
    const maskLen = this.client ? 4 : 0;
    const h = Buffer.allocUnsafe(head + maskLen);
    h[0] = 0x80 | op;
    if (len < 126) h[1] = len;
    else if (len < 65536) { h[1] = 126; h.writeUInt16BE(len, 2); }
    else { h[1] = 127; h.writeUInt32BE(0, 2); h.writeUInt32BE(len, 6); }
    if (this.client) {
      h[1] |= 0x80; const m = crypto.randomBytes(4); m.copy(h, head);
      const p = Buffer.allocUnsafe(len); for (let i = 0; i < len; i++) p[i] = data[i] ^ m[i & 3];
      data = p;
    }
    this.socket.write(h);
    return this.socket.write(data);
  }

  // text for strings, binary for buffers. Returns false when the socket's buffer is full (wait for 'drain').
  send(data) {
    if (this.state !== 'open') return false;
    if (typeof data === 'string') return this._frame(OP.TEXT, Buffer.from(data, 'utf8'));
    return this._frame(OP.BIN, Buffer.isBuffer(data) ? data : Buffer.from(data));
  }
  ping(data = Buffer.alloc(0)) { if (this.state === 'open') this._frame(OP.PING, data); }
  pause() { this.socket.pause(); }
  resume() { this.socket.resume(); }

  close(code = 1000, reason = '') {
    if (this.state !== 'open') return;
    this.state = 'closing';
    const r = Buffer.from(String(reason).slice(0, 100), 'utf8');
    const p = Buffer.allocUnsafe(2 + r.length); p.writeUInt16BE(code, 0); r.copy(p, 2);
    this._frame(OP.CLOSE, p);
    this._armKill();
  }
  // protocol error: close with a code and drop the connection
  _fail(code, reason) {
    this.failure = { code, reason };
    if (this.state === 'open') this.close(code, reason);
    this.buf = Buffer.alloc(0);
    this.state = 'closing';
    setTimeout(() => this.socket.destroy(), 50);
  }
  _armKill() { if (!this.closeTimer) { this.closeTimer = setTimeout(() => this.socket.destroy(), 3000); this.closeTimer.unref?.(); } }
  terminate() { this.socket.destroy(); }
  _finish(code, reason) {
    if (this.state === 'closed') return;
    this.state = 'closed';
    if (this.closeTimer) clearTimeout(this.closeTimer);
    const c = this.peerClose || this.failure || { code, reason };
    this.emit('close', c.code, c.reason);
  }
}

// Answers an HTTP upgrade request. Calls done(ws) on success; answers 400 otherwise.
export function upgrade(req, socket, head, opts, done) {
  const key = req.headers['sec-websocket-key'];
  const ok = req.method === 'GET' && String(req.headers.upgrade || '').toLowerCase() === 'websocket' &&
    /(^|,)\s*upgrade\s*(,|$)/i.test(String(req.headers.connection || '')) && req.headers['sec-websocket-version'] === '13' &&
    typeof key === 'string' && Buffer.from(key, 'base64').length === 16;
  if (!ok) { socket.end('HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'); return; }
  socket.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ' + acceptKey(key) + '\r\n\r\n');
  const ws = new WebSocketConn(socket, opts);
  done(ws);
  if (head && head.length) ws._data(head);
}

// Minimal client (tests only): connects to ws://host:port/path.
export function connect(url, opts = {}) {
  return new Promise((resolve, reject) => {
    const u = new URL(url);
    const sock = net.connect(Number(u.port || 80), u.hostname);
    const key = crypto.randomBytes(16).toString('base64');
    let got = Buffer.alloc(0);
    sock.once('error', reject);
    sock.on('connect', () => sock.write(`GET ${u.pathname}${u.search} HTTP/1.1\r\nHost: ${u.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n${opts.headers || ''}\r\n`));
    const onData = d => {
      got = Buffer.concat([got, d]);
      const i = got.indexOf('\r\n\r\n'); if (i < 0) return;
      sock.removeListener('data', onData);
      const head = got.subarray(0, i).toString();
      if (!/^HTTP\/1\.1 101/.test(head) || !head.includes(acceptKey(key))) { sock.destroy(); return reject(new Error('upgrade refused: ' + head.split('\r\n')[0])); }
      const ws = new WebSocketConn(sock, { client: true, maxMessage: opts.maxMessage || 64 * 1024 * 1024 });
      resolve(ws);
      const rest = got.subarray(i + 4); if (rest.length) ws._data(rest);
    };
    sock.on('data', onData);
  });
}

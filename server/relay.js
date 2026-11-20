// Hallow Assist relay: pairs the person who needs help ("host") with their helper ("viewer") by a 9-digit
// session code and passes their encrypted frames between them. It never sees screen pictures, keys or chat:
// the two ends agree a key between themselves (ECDH bound to the one-time password) and everything after
// that is AES-256-GCM. The relay only reads its own small JSON control messages. Payloads are never logged.
// It also serves the browser viewer (public/) so a helper can use any modern browser.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { upgrade } from './ws.js';

export const VERSION = '1.1';
const HERE = path.dirname(fileURLToPath(import.meta.url));

export function config(env = process.env) {
  const num = (v, d) => { const n = Number(v); return Number.isFinite(n) && n > 0 ? n : d; };
  return {
    port: num(env.PORT, 8080),
    host: env.HOST || '0.0.0.0',
    publicUrl: String(env.PUBLIC_URL || '').replace(/\/+$/, ''),
    // 'cloudflare': trust CF-Connecting-IP, but only from the tunnel container / this machine (like Gloam)
    trustProxy: String(env.TRUST_PROXY || 'none').toLowerCase(),
    maxMessage: num(env.MAX_MESSAGE_BYTES, 4 * 1024 * 1024),
    codeTtlMs: num(env.CODE_TTL_SECONDS, 600) * 1000,
    idleMs: num(env.IDLE_TIMEOUT_SECONDS, 60) * 1000,
    maxSessions: num(env.MAX_SESSIONS, 200),
    maxConnPerIp: num(env.MAX_CONNECTIONS_PER_IP, 20),
    guessLimit: num(env.JOIN_FAILS_PER_10_MIN, 10),
    joinLimit: num(env.JOINS_PER_10_MIN, 40),
    log: env.LOG !== 'off',
  };
}

// The 9-digit code of an unattended PC comes from a secret only that PC knows, so nobody can take it over.
export function persistentCode(secret) {
  const h = crypto.createHash('sha256').update('hallow-assist/v1/persist/' + secret, 'utf8').digest('hex');
  return String(BigInt('0x' + h.slice(0, 15)) % 900000000n + 100000000n);
}

const FROM_TUNNEL = ip => /^(127\.|::1$|172\.(1[6-9]|2\d|3[01])\.)/.test(ip);

export function createRelay(opts = {}) {
  const cfg = { ...config({}), ...opts };
  const sessions = new Map();     // code -> { code, host, viewer, persistent, expires, timer }
  const conns = new Map();        // ip -> open connections
  const joins = new Map();        // ip -> { fails: [times], all: [times] }
  const log = (...a) => { if (cfg.log) console.log(new Date().toISOString(), ...a); };
  const onForward = opts.onForward || null;   // tests only: sees exactly what is passed along

  const clientIp = req => {
    const sock = String(req.socket.remoteAddress || '').replace(/^::ffff:/, '');
    if (cfg.trustProxy === 'cloudflare' && FROM_TUNNEL(sock) && req.headers['cf-connecting-ip']) return String(req.headers['cf-connecting-ip']).trim().slice(0, 64);
    return sock;
  };

  // ---- join rate limits per address: wrong codes (guesses) and joins overall, per 10 minutes
  const WINDOW = 10 * 60 * 1000;
  const limitFor = ip => { let j = joins.get(ip); if (!j) { j = { fails: [], all: [] }; joins.set(ip, j); } const now = Date.now(); j.fails = j.fails.filter(t => now - t < WINDOW); j.all = j.all.filter(t => now - t < WINDOW); return j; };
  const limited = ip => { const j = limitFor(ip); if (j.fails.length >= cfg.guessLimit) return Math.ceil((j.fails[0] + WINDOW - Date.now()) / 1000); if (j.all.length >= cfg.joinLimit) return Math.ceil((j.all[0] + WINDOW - Date.now()) / 1000); return 0; };

  const send = (ws, obj) => { try { ws.send(JSON.stringify(obj)); } catch { } };
  const newCode = () => { for (;;) { const c = String(crypto.randomInt(100000000, 1000000000)); if (!sessions.has(c)) return c; } };

  function armExpiry(s) {
    clearTimeout(s.timer);
    if (s.persistent) return;
    s.expires = Date.now() + cfg.codeTtlMs;
    s.timer = setTimeout(() => {
      if (s.viewer) return;
      send(s.host, { t: 'expired' }); s.host.close(4000, 'code expired'); endSession(s);
    }, cfg.codeTtlMs);
    s.timer.unref?.();
  }
  function endSession(s) {
    clearTimeout(s.timer);
    if (sessions.get(s.code) === s) sessions.delete(s.code);
  }

  // forward with back-pressure: a slow receiver slows the sender down instead of filling memory
  function forward(from, to, data, role) {
    if (onForward) onForward(role, data);
    to.send(data);
    if (to.bufferedAmount > 8 * 1024 * 1024 && !from.paused) {
      from.paused = true; from.pause();
      // resume when the receiver catches up - or leaves: a receiver that closes never drains, and the sender
      // would otherwise stay unread until the idle timeout drops it
      const go = () => { to.off('drain', go); to.off('close', go); if (from.paused) { from.paused = false; from.resume(); } };
      to.on('drain', go); to.on('close', go);
    }
  }

  function onConnection(ws, ip) {
    let role = null, session = null;
    const hello = setTimeout(() => { if (!role) ws.close(4002, 'no hello'); }, 10000); hello.unref?.();
    ws.on('message', (data, binary) => {
      if (!role) {
        clearTimeout(hello);
        let m; try { m = binary ? null : JSON.parse(data); } catch { m = null; }
        if (!m || typeof m !== 'object') return ws.close(1008, 'hello expected');
        if (m.t === 'host') {
          if (sessions.size >= cfg.maxSessions) { send(ws, { t: 'error', reason: 'full' }); return ws.close(4003, 'relay full'); }
          let code, persistent = false;
          if (typeof m.persist === 'string') {
            if (!/^[0-9a-f]{64}$/.test(m.persist)) return ws.close(1008, 'bad id');
            code = persistentCode(m.persist); persistent = true;
            const old = sessions.get(code);
            if (old) { old.replaced = true; old.host.close(4004, 'replaced'); if (old.viewer) old.viewer.close(4005, 'host left'); endSession(old); }
          } else code = newCode();
          role = 'host';
          session = { code, host: ws, viewer: null, persistent, timer: null };
          sessions.set(code, session); armExpiry(session);
          send(ws, { t: 'code', code, ttl: persistent ? 0 : Math.round(cfg.codeTtlMs / 1000) });
          log('session opened', persistent ? '(unattended)' : '', 'sessions:', sessions.size);
          return;
        }
        if (m.t === 'join') {
          const wait = limited(ip);
          if (wait) { send(ws, { t: 'error', reason: 'rate-limited', retryAfter: wait }); log('join refused: too many tries from one address'); return ws.close(4029, 'too many tries'); }
          const j = limitFor(ip); j.all.push(Date.now());
          const code = String(m.code || '').replace(/\D/g, '');
          const s = /^\d{9}$/.test(code) ? sessions.get(code) : null;
          if (!s) { j.fails.push(Date.now()); send(ws, { t: 'error', reason: 'not-found' }); return ws.close(4404, 'no such code'); }
          if (s.viewer) { send(ws, { t: 'error', reason: 'busy' }); return ws.close(4009, 'already in use'); }
          role = 'viewer'; session = s; s.viewer = ws; clearTimeout(s.timer);
          send(ws, { t: 'paired' }); send(s.host, { t: 'paired' });
          log('paired');
          return;
        }
        return ws.close(1008, 'unknown hello');
      }
      const s = session;
      if (!binary) {
        let m; try { m = JSON.parse(data); } catch { return ws.close(1008, 'bad control message'); }
        if (m.t === 'ping') return send(ws, { t: 'pong' });
        if (m.t === 'kick' && role === 'host' && s.viewer) { const v = s.viewer; s.viewer = null; send(v, { t: 'kicked' }); v.close(4001, 'kicked'); armExpiry(s); return; }
        return;
      }
      const other = role === 'host' ? s.viewer : s.host;
      if (!other) return;   // nobody to pass it to (e.g. the viewer just left)
      if (!other.open) return;
      forward(ws, other, data, role);
    });
    ws.on('close', () => {
      clearTimeout(hello);
      const n = (conns.get(ip) || 1) - 1; if (n <= 0) conns.delete(ip); else conns.set(ip, n);
      if (!session) return;
      const s = session;
      if (role === 'host') {
        if (s.replaced) return;
        if (s.viewer) { send(s.viewer, { t: 'peer-left' }); s.viewer.close(4005, 'host left'); }
        endSession(s);
        log('session closed', 'sessions:', sessions.size);
      } else if (role === 'viewer' && s.viewer === ws) {
        s.viewer = null;
        if (s.host.open) { send(s.host, { t: 'peer-left' }); armExpiry(s); }
      }
    });
  }

  // ---- static files: the browser viewer
  const PUB = path.join(HERE, 'public');
  const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.png': 'image/png', '.json': 'application/json', '.webmanifest': 'application/manifest+json', '.svg': 'image/svg+xml' };
  const FILES = new Set(['index.html', 'viewer.js', 'proto.js', 'keys.js', 'viewer.css', 'icon.png', 'manifest.webmanifest']);
  function headers(req, type) {
    const host = String(req.headers.host || '').replace(/[^a-zA-Z0-9.:\-\[\]]/g, '');
    return {
      'Content-Type': type,
      'Cache-Control': 'no-cache',
      'X-Content-Type-Options': 'nosniff',
      'X-Frame-Options': 'DENY',
      'Referrer-Policy': 'no-referrer',
      'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
      'Content-Security-Policy': `default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob: data:; connect-src 'self' ws://${host} wss://${host}; frame-ancestors 'none'; base-uri 'none'; form-action 'none'`,
    };
  }
  const server = http.createServer((req, res) => {
    const u = new URL(req.url, 'http://x');
    if (req.method !== 'GET' && req.method !== 'HEAD') { res.writeHead(405); return res.end(); }
    if (u.pathname === '/healthz') {
      res.writeHead(200, { ...headers(req, 'application/json'), 'Cache-Control': 'no-store' });
      return res.end(JSON.stringify({ ok: true, app: 'Hallow Assist relay', version: VERSION, sessions: sessions.size, uptime: Math.round(process.uptime()) }));
    }
    if (u.pathname === '/config.json') {
      res.writeHead(200, headers(req, 'application/json'));
      return res.end(JSON.stringify({ publicUrl: cfg.publicUrl || null, version: VERSION }));
    }
    let name = u.pathname === '/' ? 'index.html' : u.pathname.slice(1);
    if (!FILES.has(name)) { res.writeHead(404, headers(req, 'text/plain; charset=utf-8')); return res.end('Not found'); }
    fs.readFile(path.join(PUB, name), (err, body) => {
      if (err) { res.writeHead(404, headers(req, 'text/plain; charset=utf-8')); return res.end('Not found'); }
      res.writeHead(200, headers(req, TYPES[path.extname(name)] || 'application/octet-stream'));
      res.end(req.method === 'HEAD' ? undefined : body);
    });
  });
  server.on('upgrade', (req, socket, head) => {
    socket.on('error', () => {});
    const u = new URL(req.url, 'http://x');
    if (u.pathname !== '/ws') { socket.end('HTTP/1.1 404 Not Found\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'); return; }
    const ip = clientIp(req);
    const n = conns.get(ip) || 0;
    if (n >= cfg.maxConnPerIp) { socket.end('HTTP/1.1 429 Too Many Requests\r\nConnection: close\r\nContent-Length: 0\r\n\r\n'); return; }
    upgrade(req, socket, head, { maxMessage: cfg.maxMessage }, ws => { conns.set(ip, n + 1); all.add(ws); ws.on('close', () => all.delete(ws)); onConnection(ws, ip); });
  });

  // keep-alive: ping everyone; drop connections that have been silent longer than the idle timeout
  const all = new Set();
  const beat = setInterval(() => {
    const now = Date.now();
    for (const ws of all) { if (now - ws.lastSeen > cfg.idleMs) ws.terminate(); else ws.ping(); }
  }, Math.min(20000, Math.max(200, cfg.idleMs / 3)));
  beat.unref?.();
  server.on('close', () => clearInterval(beat));

  return {
    server, sessions, cfg,
    listen: (port = cfg.port, host = cfg.host) => new Promise(r => server.listen(port, host, () => r(server.address().port))),
    close: () => new Promise(r => { for (const ws of all) ws.terminate(); server.close(() => r()); server.closeAllConnections?.(); }),
  };
}

// node relay.js
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const cfg = config();
  const relay = createRelay(cfg);
  relay.listen().then(p => console.log(`Hallow Assist relay ${VERSION} on ${cfg.host}:${p}` + (cfg.publicUrl ? ` (public address ${cfg.publicUrl})` : '')));
  const stop = () => relay.close().then(() => process.exit(0));
  process.on('SIGTERM', stop); process.on('SIGINT', stop);
}

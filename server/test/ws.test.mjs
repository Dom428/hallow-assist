// WebSocket framing tests (RFC 6455): handshake, masking, fragmentation, ping/pong, close, oversize, UTF-8.
//   node --test test/
import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import net from 'node:net';
import crypto from 'node:crypto';
import { upgrade, connect, acceptKey } from '../ws.js';

function echoServer(opts = {}) {
  const srv = http.createServer();
  const conns = [];
  srv.on('upgrade', (req, sock, head) => upgrade(req, sock, head, { maxMessage: opts.maxMessage || 1024 }, ws => {
    conns.push(ws);
    ws.on('message', (d, bin) => ws.send(bin ? d : 'echo:' + d));
  }));
  return new Promise(r => srv.listen(0, '127.0.0.1', () => r({ srv, conns, port: srv.address().port })));
}

// raw client: handshake, then hand-built frames
function raw(port) {
  return new Promise((resolve, reject) => {
    const s = net.connect(port, '127.0.0.1');
    const key = crypto.randomBytes(16).toString('base64');
    let buf = Buffer.alloc(0), upgraded = false;
    const frames = []; const waiters = [];
    s.on('error', reject);
    s.on('connect', () => s.write(`GET /ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: ${key}\r\nSec-WebSocket-Version: 13\r\n\r\n`));
    s.on('data', d => {
      buf = Buffer.concat([buf, d]);
      if (!upgraded) {
        const i = buf.indexOf('\r\n\r\n'); if (i < 0) return;
        const head = buf.subarray(0, i).toString(); buf = buf.subarray(i + 4); upgraded = true;
        assert.match(head, /^HTTP\/1\.1 101/); assert.ok(head.includes(acceptKey(key)));
        resolve(api);
      }
      for (;;) {
        if (buf.length < 2) break;
        let len = buf[1] & 0x7f, off = 2;
        if (len === 126) { if (buf.length < 4) break; len = buf.readUInt16BE(2); off = 4; }
        else if (len === 127) { if (buf.length < 10) break; len = buf.readUInt32BE(6); off = 10; }
        if (buf.length < off + len) break;
        frames.push({ fin: !!(buf[0] & 0x80), op: buf[0] & 0x0f, masked: !!(buf[1] & 0x80), data: buf.subarray(off, off + len) });
        buf = buf.subarray(off + len);
        while (waiters.length && frames.length) waiters.shift()(frames.shift());
      }
    });
    let closed = false; s.on('close', () => { closed = true; while (waiters.length) waiters.shift()(null); });
    const api = {
      sock: s,
      send(op, data, { fin = true, mask = true, rsv = 0 } = {}) {
        data = Buffer.from(data);
        const len = data.length;
        const h = [((fin ? 0x80 : 0) | rsv | op)];
        if (len < 126) h.push((mask ? 0x80 : 0) | len); else if (len < 65536) h.push((mask ? 0x80 : 0) | 126, len >> 8, len & 255);
        else { h.push((mask ? 0x80 : 0) | 127, 0, 0, 0, 0, (len >>> 24) & 255, (len >>> 16) & 255, (len >>> 8) & 255, len & 255); }
        let body = data;
        if (mask) { const m = crypto.randomBytes(4); body = Buffer.alloc(len); for (let i = 0; i < len; i++) body[i] = data[i] ^ m[i & 3]; s.write(Buffer.concat([Buffer.from(h), m, body])); }
        else s.write(Buffer.concat([Buffer.from(h), body]));
      },
      next(ms = 2000) { return new Promise(r => { if (frames.length) return r(frames.shift()); if (closed) return r(null); waiters.push(r); setTimeout(() => r('timeout'), ms); }); },
      closed: () => closed,
      waitClosed(ms = 2000) { return new Promise(r => { if (closed) return r(true); s.on('close', () => r(true)); setTimeout(() => r(false), ms); }); },
    };
  });
}

test('handshake and masked text/binary echo; server frames are not masked', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  c.send(1, 'hello');
  let f = await c.next(); assert.equal(f.op, 1); assert.equal(f.masked, false); assert.equal(f.data.toString(), 'echo:hello');
  c.send(2, Buffer.from([1, 2, 3, 255]));
  f = await c.next(); assert.equal(f.op, 2); assert.deepEqual([...f.data], [1, 2, 3, 255]);
  c.sock.destroy(); srv.close();
});

test('bad upgrade requests get 400', async () => {
  const { srv, port } = await echoServer();
  const reply = await new Promise(r => { const s = net.connect(port, '127.0.0.1', () => s.write('GET /ws HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n\r\n')); let b = ''; s.on('data', d => b += d); s.on('close', () => r(b)); });
  assert.match(reply, /^HTTP\/1\.1 400/);
  srv.close();
});

test('fragmented message is joined; control frames may come between fragments', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  c.send(1, 'frag', { fin: false });
  c.send(9, 'pp');                      // ping in the middle
  c.send(0, 'men', { fin: false });
  c.send(0, 'ted', { fin: true });
  let f = await c.next(); assert.equal(f.op, 10); assert.equal(f.data.toString(), 'pp');   // pong with the same data
  f = await c.next(); assert.equal(f.op, 1); assert.equal(f.data.toString(), 'echo:fragmented');
  c.sock.destroy(); srv.close();
});

test('unmasked client frame is a protocol error (1002)', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  c.send(1, 'nope', { mask: false });
  const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1002);
  assert.ok(await c.waitClosed());
  srv.close();
});

test('continuation without a start, reserved bits and bad opcodes are refused', async () => {
  const { srv, port } = await echoServer();
  for (const [op, opts] of [[0, {}], [1, { rsv: 0x40 }], [3, {}], [9, { fin: false }]]) {
    const c = await raw(port);
    c.send(op, 'x', opts);
    const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1002, 'op ' + op);
    c.sock.destroy();
  }
  srv.close();
});

test('message over the limit closes with 1009 (single frame and fragmented)', async () => {
  const { srv, port } = await echoServer({ maxMessage: 1024 });
  let c = await raw(port);
  c.send(2, Buffer.alloc(2000));
  let f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1009);
  c.sock.destroy();
  c = await raw(port);
  c.send(2, Buffer.alloc(700), { fin: false }); c.send(0, Buffer.alloc(700), { fin: true });
  f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1009);
  c.sock.destroy();
  // exactly at the limit is fine
  c = await raw(port);
  c.send(2, Buffer.alloc(1024)); f = await c.next(); assert.equal(f.op, 2); assert.equal(f.data.length, 1024);
  c.sock.destroy(); srv.close();
});

test('control frame longer than 125 bytes is refused', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  c.send(9, Buffer.alloc(126));
  const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1002);
  c.sock.destroy(); srv.close();
});

test('invalid UTF-8 text closes with 1007', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  c.send(1, Buffer.from([0xc3, 0x28]));
  const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1007);
  c.sock.destroy(); srv.close();
});

test('close handshake: server echoes the code and closes the TCP connection', async () => {
  const { srv, port, conns } = await echoServer();
  const c = await raw(port);
  const closed = new Promise(r => setTimeout(() => conns[0].on('close', (code, reason) => r({ code, reason })), 20));
  await new Promise(r => setTimeout(r, 30));
  const p = Buffer.alloc(2 + 3); p.writeUInt16BE(1000, 0); p.write('bye', 2);
  c.send(8, p);
  const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1000);
  assert.ok(await c.waitClosed());
  assert.deepEqual(await closed, { code: 1000, reason: 'bye' });
  srv.close();
});

test('bad close codes are refused', async () => {
  const { srv, port } = await echoServer();
  const c = await raw(port);
  const p = Buffer.alloc(2); p.writeUInt16BE(1005, 0); c.send(8, p);
  const f = await c.next(); assert.equal(f.op, 8); assert.equal(f.data.readUInt16BE(0), 1002);
  c.sock.destroy(); srv.close();
});

test('16-bit and 64-bit lengths; the test client masks and reads unmasked frames', async () => {
  const { srv, port } = await echoServer({ maxMessage: 200000 });
  const ws = await connect(`ws://127.0.0.1:${port}/ws`);
  const got = [];
  ws.on('message', (d, bin) => got.push(bin ? d.length : d));
  ws.send(Buffer.alloc(300)); ws.send(Buffer.alloc(70000)); ws.send('hi');
  await new Promise(r => setTimeout(r, 300));
  assert.deepEqual(got, [300, 70000, 'echo:hi']);
  ws.close(); srv.close();
});

test('server ping gets a pong from the client side', async () => {
  const { srv, port, conns } = await echoServer();
  const ws = await connect(`ws://127.0.0.1:${port}/ws`);
  await new Promise(r => setTimeout(r, 30));
  const pong = new Promise(r => conns[0].on('pong', d => r(d.toString())));
  conns[0].ping(Buffer.from('beat'));
  assert.equal(await pong, 'beat');
  ws.close(); srv.close();
});

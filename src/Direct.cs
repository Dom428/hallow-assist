// Hallow Assist 1.3: a direct connection when both PCs are on the same network. The helper's app listens on its home-network
// address; the PC being helped connects OUT to it (so the person being helped never sees a firewall question). The address
// and a one-time 32-byte token travel inside the sealed channel, so only the real helper's app can take the connection.
// Over the direct link go exactly the same sealed messages (AES-256-GCM, counters that must go up by one), so nothing about
// the encryption changes - only the path. The relay link stays open for the relay's own control messages.
//
// Switching paths without breaking the counter order: a side stops sealing new messages for a moment, waits until
// everything already queued for the relay has been sealed, sends a sealed "switch" as its LAST message through the relay,
// waits for that to be sealed too, then sends everything after it over the direct link. The other side keeps what arrives
// over the direct link until it has read that "switch" from the relay, so messages are always opened in counter order.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;

namespace HallowAssist
{
    public interface ISealedSender
    {
        void SendSealed(Func<byte[]> make, int sizeHint);
        long Queued { get; }
    }

    public static class DirectNet
    {
        public const int MaxEndpoints = 4, TokenBytes = 32;
        public static volatile bool TestLoopback;   // tests only (test mode): offer and accept 127.0.0.1
        static bool Loopback { get { return TestLoopback && AssistSettings.TestDir != null; } }
        // home-network addresses only (10/8, 172.16/12, 192.168/16) - the PC being helped never connects anywhere else
        public static bool IsPrivate(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = a.GetAddressBytes();
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 127) return Loopback;
            return false;
        }
        public static List<IPAddress> LocalAddresses()
        {
            if (Loopback) return new List<IPAddress> { IPAddress.Loopback };
            var o = new List<IPAddress>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                        if (IsPrivate(u.Address) && !o.Contains(u.Address)) o.Add(u.Address);
                }
            }
            catch { }
            return o.Take(MaxEndpoints).ToList();
        }
        // "192.168.1.20:50123" -> endpoint, only home-network addresses and ports above 1024
        public static List<IPEndPoint> ParseEndpoints(IEnumerable<string> items)
        {
            var o = new List<IPEndPoint>();
            foreach (var s in items ?? new string[0])
            {
                if (s == null || o.Count >= MaxEndpoints) continue;
                var i = s.LastIndexOf(':'); if (i <= 0) continue;
                IPAddress a; int port;
                if (!IPAddress.TryParse(s.Substring(0, i), out a) || !int.TryParse(s.Substring(i + 1), out port)) continue;
                if (!IsPrivate(a) || port <= 1024 || port > 65535) continue;
                o.Add(new IPEndPoint(a, port));
            }
            return o;
        }
    }

    // one TCP connection carrying sealed messages: 4-byte big-endian length, then the message. Sealing happens in send
    // order on one thread, like the relay link, so counters leave in order.
    public sealed class DirectLink : ISealedSender, IDisposable
    {
        public const int MaxMessage = 16 * 1024 * 1024;
        readonly Socket sock; readonly NetworkStream stream;
        readonly BlockingCollection<Func<byte[]>> queue = new BlockingCollection<Func<byte[]>>();
        long queuedBytes; int closed;
        public event Action<byte[]> Frame;
        public event Action<string> Closed;
        public long BytesIn, BytesOut;
        public long Queued { get { return Interlocked.Read(ref queuedBytes); } }
        public DirectLink(Socket s)
        {
            sock = s; sock.NoDelay = true; sock.ReceiveTimeout = 0; sock.SendTimeout = 30000;
            stream = new NetworkStream(sock, true);
        }
        public void Start()
        {
            new Thread(ReceiveLoop) { IsBackground = true, Name = "direct receive" }.Start();
            new Thread(SendLoop) { IsBackground = true, Name = "direct send" }.Start();
        }
        public void SendSealed(Func<byte[]> make, int sizeHint)
        {
            if (closed != 0) return;
            Interlocked.Add(ref queuedBytes, sizeHint);
            try { queue.Add(() => { Interlocked.Add(ref queuedBytes, -sizeHint); return make(); }); } catch { }
        }
        void SendLoop()
        {
            try
            {
                foreach (var make in queue.GetConsumingEnumerable())
                {
                    var data = make();
                    var head = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
                    stream.Write(head, 0, 4); stream.Write(data, 0, data.Length);
                    Interlocked.Add(ref BytesOut, data.Length + 4);
                }
            }
            catch (Exception e) { Close("direct send: " + e.Message); }
        }
        void ReceiveLoop()
        {
            try
            {
                var head = new byte[4];
                for (;;)
                {
                    if (!ReadExactly(stream, head, 4)) { Close("the other PC closed the direct connection"); return; }
                    int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
                    if (len <= 0 || len > MaxMessage) { Close("a direct message had a bad size"); return; }
                    var data = new byte[len];
                    if (!ReadExactly(stream, data, len)) { Close("the direct connection was cut"); return; }
                    Interlocked.Add(ref BytesIn, len + 4);
                    var h = Frame; if (h != null) h(data);
                }
            }
            catch (Exception e) { Close("direct receive: " + e.Message); }
        }
        public static bool ReadExactly(Stream s, byte[] b, int n)
        {
            int got = 0; while (got < n) { int r = s.Read(b, got, n - got); if (r <= 0) return false; got += r; }
            return true;
        }
        public bool IsOpen { get { return closed == 0; } }
        public void Close(string reason)
        {
            if (Interlocked.Exchange(ref closed, 1) == 1) return;
            try { queue.CompleteAdding(); } catch { }
            try { stream.Dispose(); } catch { }
            try { sock.Close(); } catch { }
            var h = Closed; if (h != null) try { h(reason); } catch { }
        }
        public void Dispose() { Close("closed"); }
    }

    // the helper's side: listen on this PC's home-network addresses for one connection that knows the token
    public sealed class DirectListener : IDisposable
    {
        readonly List<TcpListener> listeners = new List<TcpListener>();
        public readonly List<string> Endpoints = new List<string>();
        public readonly byte[] Token = new byte[DirectNet.TokenBytes];
        public int Refused;
        public DirectListener()
        {
            using (var r = RandomNumberGenerator.Create()) r.GetBytes(Token);
            foreach (var a in DirectNet.LocalAddresses())
            {
                try { var l = new TcpListener(a, 0); l.Start(4); listeners.Add(l); Endpoints.Add(a + ":" + ((IPEndPoint)l.LocalEndpoint).Port); }
                catch { }
            }
        }
        // the first connection that sends the right token within the time; anything else is closed (at most 10 tries)
        public DirectLink Accept(int timeoutMs)
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < until && Refused < 10 && listeners.Count > 0)
            {
                foreach (var l in listeners)
                {
                    bool pending; try { pending = l.Pending(); } catch { pending = false; }
                    if (!pending) continue;
                    Socket s = null;
                    try
                    {
                        s = l.AcceptSocket(); s.ReceiveTimeout = 3000;
                        var got = new byte[DirectNet.TokenBytes];
                        using (var ns = new NetworkStream(s, false))
                            if (DirectLink.ReadExactly(ns, got, got.Length) && Kdf.Equal(got, Token)) return new DirectLink(s);
                    }
                    catch { }
                    Refused++; try { if (s != null) s.Close(); } catch { }
                }
                Thread.Sleep(30);
            }
            return null;
        }
        public void Dispose() { foreach (var l in listeners) try { l.Stop(); } catch { } listeners.Clear(); }
    }

    // the side being helped: connect out to the first of the helper's addresses that answers, and prove it with the token
    public static class DirectConnector
    {
        public static DirectLink Connect(List<IPEndPoint> endpoints, byte[] token, int timeoutMs)
        {
            if (endpoints == null || endpoints.Count == 0 || token == null || token.Length != DirectNet.TokenBytes) return null;
            Socket winner = null; var gate = new object(); var done = new CountdownEvent(endpoints.Count);
            var all = new List<Socket>();
            foreach (var ep in endpoints)
            {
                var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp); lock (gate) all.Add(s);
                var e = ep;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        var r = s.BeginConnect(e, null, null);
                        if (r.AsyncWaitHandle.WaitOne(timeoutMs)) { s.EndConnect(r); if (s.Connected) lock (gate) if (winner == null) winner = s; }
                    }
                    catch { }
                    finally { done.Signal(); }
                });
            }
            done.Wait(timeoutMs + 500);
            Socket w; lock (gate) { w = winner; foreach (var s in all) if (s != w) try { s.Close(); } catch { } }
            if (w == null) return null;
            try { w.Send(token); return new DirectLink(w); }
            catch { try { w.Close(); } catch { } return null; }
        }
    }

    // which path sealed messages take, and the hand-over from the relay to the direct link (see the top of this file)
    public sealed class Transport : ISealedSender
    {
        readonly ISealedSender relay; ISealedSender direct;
        int mode;   // 0 relay, 1 switching (new messages wait), 2 direct
        readonly List<Tuple<Func<byte[]>, int>> held = new List<Tuple<Func<byte[]>, int>>();
        readonly object gate = new object();
        public Transport(ISealedSender relay) { this.relay = relay; }
        public bool IsDirect { get { return mode == 2; } }
        public long Queued { get { var d = direct; return relay.Queued + (d == null ? 0 : d.Queued); } }
        public void SendSealed(Func<byte[]> make, int sizeHint)
        {
            ISealedSender to;
            lock (gate)
            {
                if (mode == 1) { held.Add(Tuple.Create(make, sizeHint)); return; }
                to = mode == 2 ? direct : relay;
                to.SendSealed(make, sizeHint);   // inside the lock: queue order = seal order, also across the switch
            }
        }
        // true = everything from now on goes over d. false = nothing changed (still the relay).
        public bool SwitchTo(ISealedSender d, Func<byte[]> makeSwitch, int drainMs = 8000)
        {
            lock (gate) { if (mode != 0) return false; mode = 1; }
            if (!Drained(drainMs)) { Back(); return false; }
            relay.SendSealed(makeSwitch, 64);   // the last sealed message through the relay
            Drained(60000);                     // once sent, the other side expects the rest elsewhere: wait for it to be sealed
            lock (gate)
            {
                direct = d; mode = 2;
                foreach (var h in held) d.SendSealed(h.Item1, h.Item2);
                held.Clear();
            }
            return true;
        }
        void Back() { lock (gate) { mode = 0; foreach (var h in held) relay.SendSealed(h.Item1, h.Item2); held.Clear(); } }
        bool Drained(int ms) { var until = DateTime.UtcNow.AddMilliseconds(ms); while (relay.Queued > 0) { if (DateTime.UtcNow > until) return false; Thread.Sleep(5); } return true; }

        // ---- receiving: what comes over the direct link waits until the other side's "switch" was read from the relay
        readonly object rg = new object(); readonly Queue<byte[]> inbox = new Queue<byte[]>(); long inboxBytes; bool peerSwitched, draining;
        public Action<byte[]> Deliver;     // the session's OnSealed
        public Action<string> Overflow;    // the other side sent a lot directly without ever switching
        public void OnDirectFrame(byte[] b)
        {
            lock (rg)
            {
                if (!peerSwitched || draining)
                {
                    inbox.Enqueue(b); inboxBytes += b.Length;
                    if (inboxBytes > 64L * 1024 * 1024) { var o = Overflow; if (o != null) o("too much arrived directly before the switch"); }
                    return;
                }
            }
            var d = Deliver; if (d != null) d(b);
        }
        public void PeerSwitched()
        {
            lock (rg) { if (peerSwitched || draining) return; draining = true; }
            for (;;)
            {
                byte[] b;
                lock (rg) { if (inbox.Count == 0) { draining = false; peerSwitched = true; inboxBytes = 0; return; } b = inbox.Dequeue(); }
                var d = Deliver; if (d != null) d(b);
            }
        }
        public bool PeerIsDirect { get { lock (rg) return peerSwitched; } }
    }
}

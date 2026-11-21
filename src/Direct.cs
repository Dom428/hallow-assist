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
//
// v1.4: their PC only switches once the other end has proved it knows this session's token (an HMAC of it, different for
// each direction), only tries addresses on its own subnets, and a direct link that drops mid-session falls back to the relay:
// the side that notices closes the link, and its first sealed message on the relay is a "relay-again" marker. The receiver
// accepts a jump in the counter ONLY for that authenticated marker (the messages lost with the direct link), then the strict
// one-by-one order applies again.
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
        // v1.4: what each end sends over a new direct connection - never the token itself, and different for each direction
        // (so a device that echoes what it gets doesn't pass)
        public static byte[] Proof(byte[] token, string who) { return Kdf.Hmac(token, Kdf.Utf8("Hallow Assist direct v1.4 " + who)); }
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
        // v1.4: this PC's network adapters that are up, with a home-network IPv4 address (one entry per address)
        public static List<NetAdapter> Adapters()
        {
            if (Loopback) return new List<NetAdapter> { new NetAdapter { Address = IPAddress.Loopback, Mask = IPAddress.Parse("255.0.0.0"), Gateway = true, Name = "test", Type = NetworkInterfaceType.Ethernet, Mac = null } };
            var o = new List<NetAdapter>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    var ip = ni.GetIPProperties();
                    bool gw = ip.GatewayAddresses.Any(g => g.Address != null && g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    string mac = null; try { var pa = ni.GetPhysicalAddress().GetAddressBytes(); if (pa.Length == 6 && pa.Any(x => x != 0)) mac = BitConverter.ToString(pa).Replace("-", ""); } catch { }
                    foreach (var u in ip.UnicastAddresses)
                    {
                        if (!IsPrivate(u.Address) || o.Any(x => x.Address.Equals(u.Address))) continue;
                        IPAddress mask = null; try { mask = u.IPv4Mask; } catch { }
                        o.Add(new NetAdapter { Address = u.Address, Mask = mask, Gateway = gw, Name = ni.Name, Description = ni.Description, Type = ni.NetworkInterfaceType, Mac = mac });
                    }
                }
            }
            catch { }
            return o;
        }
        // v1.4: Hyper-V / WSL / Docker / VirtualBox / VMware / VPN adapters - they must not crowd out the real home network
        public static bool LooksVirtual(NetAdapter a)
        {
            var s = ((a.Name ?? "") + " " + (a.Description ?? "")).ToLowerInvariant();
            foreach (var w in new[] { "virtual", "hyper-v", "vethernet", "wsl", "docker", "vmware", "virtualbox", "vbox", "tap-", "tap adapter", "wintun", "wireguard", "tailscale", "zerotier", "hamachi", "npcap", "loopback", "bluetooth" })
                if (s.Contains(w)) return true;
            var b = a.Address == null ? null : a.Address.GetAddressBytes();
            return b != null && b.Length == 4 && b[0] == 192 && b[1] == 168 && b[2] == 56;   // VirtualBox host-only
        }
        // lower is better: up adapters with a default gateway that aren't virtual come first, then real ones without a gateway
        public static int Rank(NetAdapter a)
        {
            bool v = LooksVirtual(a);
            int r = !v && a.Gateway ? 0 : !v ? 2 : a.Gateway ? 4 : 6;
            if (a.Type != NetworkInterfaceType.Ethernet && a.Type != NetworkInterfaceType.Wireless80211 && a.Type != NetworkInterfaceType.GigabitEthernet && a.Type != NetworkInterfaceType.FastEthernetT) r++;
            return r;
        }
        public static List<NetAdapter> Ranked(IEnumerable<NetAdapter> all) { return all.Select((a, i) => new { a, i }).OrderBy(x => Rank(x.a)).ThenBy(x => x.i).Select(x => x.a).ToList(); }
        public static List<IPAddress> LocalAddresses() { return Ranked(Adapters()).Select(a => a.Address).Take(MaxEndpoints).ToList(); }
        // the MAC address of the adapter this PC most likely wakes on (the real home-network one); null if unknown
        public static string PrimaryMac() { if (AssistSettings.Fake) return "02005E102030"; var a = Ranked(Adapters()).FirstOrDefault(x => x.Mac != null && !LooksVirtual(x)); return a == null ? null : a.Mac; }

        // "192.168.1.20:50123" -> endpoint, only home-network addresses and ports from 1024 up
        public static List<IPEndPoint> ParseEndpoints(IEnumerable<string> items)
        {
            var o = new List<IPEndPoint>();
            foreach (var s in items ?? new string[0])
            {
                if (s == null || o.Count >= MaxEndpoints) continue;
                var i = s.LastIndexOf(':'); if (i <= 0) continue;
                IPAddress a; int port;
                if (!IPAddress.TryParse(s.Substring(0, i), out a) || !int.TryParse(s.Substring(i + 1), out port)) continue;
                if (!IsPrivate(a) || port < 1024 || port > 65535) continue;
                o.Add(new IPEndPoint(a, port));
            }
            return o;
        }
        // v1.4: the helper's offered addresses are only tried when they are on the same subnet as one of this PC's own adapters
        // (its mask, but never wider than /16; /24 when the mask is unknown) - a view-only helper can't make this PC probe
        // other devices or other networks
        public static int Prefix(IPAddress mask)
        {
            if (mask == null || mask.AddressFamily != AddressFamily.InterNetwork) return -1;
            var b = mask.GetAddressBytes(); int n = 0;
            for (int i = 0; i < 4; i++) for (int k = 7; k >= 0; k--) { if ((b[i] >> k & 1) == 1) n++; else goto done; }
            done: return n;
        }
        public static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
        {
            if (a == null || b == null || a.AddressFamily != AddressFamily.InterNetwork || b.AddressFamily != AddressFamily.InterNetwork) return false;
            var x = a.GetAddressBytes(); var y = b.GetAddressBytes();
            for (int i = 0; i < 4; i++) { int bits = Math.Max(0, Math.Min(8, prefix - 8 * i)); int m = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF; if ((x[i] & m) != (y[i] & m)) return false; }
            return true;
        }
        public static int EffectivePrefix(IPAddress mask) { var p = Prefix(mask); return p <= 0 ? 24 : Math.Max(16, p); }
        public static List<IPEndPoint> OnMyNetworks(IEnumerable<IPEndPoint> eps, IEnumerable<NetAdapter> mine)
        {
            var my = (mine ?? new NetAdapter[0]).ToList();
            return (eps ?? new IPEndPoint[0]).Where(e => e != null && e.Port >= 1024 && my.Any(m => SameSubnet(e.Address, m.Address, EffectivePrefix(m.Mask)))).ToList();
        }
        // directed broadcast of an adapter's subnet (192.168.1.255 for 192.168.1.20/24)
        public static IPAddress Broadcast(IPAddress a, IPAddress mask)
        {
            int p = Prefix(mask); if (p <= 0 || p > 30) p = 24;
            var b = a.GetAddressBytes();
            for (int i = 0; i < 4; i++) { int bits = Math.Max(0, Math.Min(8, p - 8 * i)); int m = bits == 0 ? 0 : (0xFF << (8 - bits)) & 0xFF; b[i] = (byte)(b[i] & m | ~m & 0xFF); }
            return new IPAddress(b);
        }
    }

    public class NetAdapter
    {
        public IPAddress Address, Mask; public bool Gateway; public string Name, Description, Mac; public NetworkInterfaceType Type;
    }

    // one TCP connection carrying sealed messages: 4-byte big-endian length, then the message. Sealing happens in send
    // order on one thread, like the relay link, so counters leave in order.
    public sealed class DirectLink : ISealedSender, IDisposable
    {
        public const int MaxMessage = 16 * 1024 * 1024;
        readonly Socket sock; readonly NetworkStream stream;
        readonly BlockingCollection<Func<byte[]>> queue = new BlockingCollection<Func<byte[]>>();
        long queuedBytes; int closed; readonly ManualResetEvent sendDone = new ManualResetEvent(false);
        readonly object sealLock = new object();   // v1.4: once Close has returned, nothing more is sealed for this link
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
            try { queue.Add(() => { var b = make(); Interlocked.Add(ref queuedBytes, -sizeHint); return b; }); } catch { }
        }
        void SendLoop()
        {
            try
            {
                foreach (var make in queue.GetConsumingEnumerable())
                {
                    byte[] data;
                    lock (sealLock) { if (closed != 0) break; data = make(); }
                    var head = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
                    stream.Write(head, 0, 4); stream.Write(data, 0, data.Length);
                    Interlocked.Add(ref BytesOut, data.Length + 4);
                }
            }
            catch (Exception e) { Close("direct send: " + e.Message); }
            finally { sendDone.Set(); }
        }
        // lets what's already queued (e.g. "bye") go out before the link closes - up to ms
        public void Drain(int ms) { try { queue.CompleteAdding(); } catch { } sendDone.WaitOne(ms); }
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
            lock (sealLock) if (Interlocked.Exchange(ref closed, 1) == 1) return;
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
                        s = l.AcceptSocket(); s.ReceiveTimeout = 3000; s.SendTimeout = 3000;
                        var got = new byte[DirectNet.TokenBytes];
                        using (var ns = new NetworkStream(s, false))
                            if (DirectLink.ReadExactly(ns, got, got.Length) && Kdf.Equal(got, DirectNet.Proof(Token, "host")))
                            {
                                var ack = DirectNet.Proof(Token, "helper"); ns.Write(ack, 0, ack.Length);   // v1.4: tells their PC it reached the right app
                                return new DirectLink(s);
                            }
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

    // the side being helped: connect out to the helper's addresses. v1.4: a connection only counts once the other end has
    // answered with the helper's proof for this session's token, within ackMs. An unrelated device that happens to accept on
    // that address and port (another network using the same private range) never can, so the session stays on the relay.
    public static class DirectConnector
    {
        public static DirectLink Connect(List<IPEndPoint> endpoints, byte[] token, int timeoutMs, int ackMs = 3000)
        {
            if (endpoints == null || endpoints.Count == 0 || token == null || token.Length != DirectNet.TokenBytes) return null;
            var hello = DirectNet.Proof(token, "host"); var want = DirectNet.Proof(token, "helper");
            Socket winner = null; bool over = false; var gate = new object(); var done = new CountdownEvent(endpoints.Count); var won = new ManualResetEvent(false);
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
                        if (!r.AsyncWaitHandle.WaitOne(timeoutMs)) return;
                        s.EndConnect(r); if (!s.Connected) return;
                        s.SendTimeout = ackMs; s.ReceiveTimeout = ackMs;
                        s.Send(hello);
                        var got = new byte[want.Length];
                        using (var ns = new NetworkStream(s, false)) if (!DirectLink.ReadExactly(ns, got, got.Length) || !Kdf.Equal(got, want)) return;
                        lock (gate) if (winner == null && !over) { winner = s; won.Set(); }
                    }
                    catch { }
                    finally { done.Signal(); }
                });
            }
            WaitHandle.WaitAny(new WaitHandle[] { won, done.WaitHandle }, timeoutMs + ackMs + 500);
            Socket w; lock (gate) { over = true; w = winner; foreach (var s in all) if (s != w) try { s.Close(); } catch { } }
            if (w == null) return null;
            try { return new DirectLink(w); }
            catch { try { w.Close(); } catch { } return null; }
        }
    }

    // which path sealed messages take, and the hand-over from the relay to the direct link (see the top of this file)
    public sealed class Transport : ISealedSender
    {
        readonly ISealedSender relay; ISealedSender direct;
        int mode;   // 0 relay, 1 switching (new messages wait), 2 direct
        bool fellBack;   // v1.4: back on the relay after the direct link went - no second try in this session
        readonly List<Tuple<Func<byte[]>, int>> held = new List<Tuple<Func<byte[]>, int>>();
        readonly object gate = new object();
        public Transport(ISealedSender relay) { this.relay = relay; }
        public bool IsDirect { get { return mode == 2; } }
        public bool FellBack { get { return fellBack; } }
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
        // v1.4: if the "switch" was queued but isn't sealed within sentMs, the switch is abandoned and everything stays on the
        // relay (before, it switched anyway, and later relay messages could overtake it)
        public bool SwitchTo(ISealedSender d, Func<byte[]> makeSwitch, int drainMs = 8000, int sentMs = 60000)
        {
            lock (gate) { if (mode != 0 || fellBack) return false; mode = 1; }
            if (!Drained(drainMs)) { Back(); return false; }
            relay.SendSealed(makeSwitch, 64);   // the last sealed message through the relay
            if (!Drained(sentMs)) { Back(); return false; }   // the other side reads "switch" eventually; it changes nothing for what we send
            lock (gate)
            {
                if (mode != 1) return false;    // the direct link went while switching
                direct = d; mode = 2;
                foreach (var h in held) d.SendSealed(h.Item1, h.Item2);
                held.Clear();
            }
            return true;
        }
        void Back() { lock (gate) { if (mode == 1) mode = 0; foreach (var h in held) relay.SendSealed(h.Item1, h.Item2); held.Clear(); } }
        // v1.4: the direct link went mid-session (close it first, so it seals nothing more). Everything goes back to the relay;
        // when sealed messages were already going directly, the first one on the relay is the marker, so the other side
        // knows to accept the counters that were lost with the direct link. True = the marker was sent.
        public bool Fallback(Func<byte[]> makeMarker)
        {
            lock (gate)
            {
                bool wasDirect = mode == 2;
                fellBack = true; mode = 0; direct = null;
                if (wasDirect) relay.SendSealed(makeMarker, 64);
                foreach (var h in held) relay.SendSealed(h.Item1, h.Item2);
                held.Clear();
                return wasDirect;
            }
        }
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
        // v1.4: the other side's marker arrived over the relay - nothing more comes directly
        public void PeerFellBack() { lock (rg) { inbox.Clear(); inboxBytes = 0; } }
        // v1.4: the marker that starts the relay again after a direct link went (sealed like every other message)
        public static byte[] MarkerPlain() { return Msg.JsonMsg("t", "relay-again"); }
        public static bool IsMarker(byte[] p)
        {
            if (p == null || p.Length < 2 || p[0] != Msg.Json) return false;
            try { return Msg.Str(Msg.ParseJson(p), "t") == "relay-again"; } catch { return false; }
        }
    }
}

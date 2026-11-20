// Hallow Assist "Get help": the side of the person being helped.
// Click Get help -> the relay gives a 9-digit code, this PC makes a 6-character one-time password. A helper who knows both
// can finish the key agreement; then this PC asks its person (Allow / Deny) and NOTHING is shared before Allow: no picture,
// no input, no clipboard, no files. While connected the always-on-top bar shows who is connected; Disconnect, the panic
// hotkey and closing the app all end it. Unattended access (your own PCs only) skips the question but still shows the bar.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace HallowAssist
{
    public enum HostState { Starting, Waiting, Checking, Asking, Connected, Ended }
    public class ConsentAnswer { public bool Allow, Control, Clipboard; }
    public interface IClip { string Get(); void Set(string text); }

    public class HostOptions
    {
        public Uri Relay; public string Quality = "auto";
        public bool Unattended; public string PersistSecret; public byte[] UnattendedKey;   // unattended: the derived key, never the password
        public ScreenSource Screen; public IInputSink Input; public IClip Clip; public string DownloadDir;
    }

    public class HostSession
    {
        readonly HostOptions o;
        RelayLink link; Channel chan; EcdhKey ecdh; SessionKeys keys; byte[] pwKey; byte[] viewerPub;
        public volatile HostState State = HostState.Starting;
        public string Code, Password, HelperName, HelperClient, Safety, EndReason;
        public volatile bool Control, Clipboard;
        public int WrongPasswords;
        public bool Unattended { get { return o.Unattended; } }
        public Transfers Files;
        public long FramesSent, BytesSent;
        int ended, consentId;
        readonly object gate = new object();

        public event Action Changed;                                        // any state change (UI refresh)
        public event Action<string, string, Action<ConsentAnswer>> ConsentNeeded;   // helper name, safety code, answer
        public event Action<string> Chat;
        public event Action<string> Note;                                   // a line for the bar / log
        public event Action<string> Ended;
        public event Action WrongPassword;
        public event Action<MonitorInfo, long, int[]> Drawing;   // v1.2: the helper draws on this screen (monitor, stroke id, points)
        public event Action DrawClear;
        public event Action<string> RestartRequested;             // v1.2: unattended only - the helper restarts this PC

        public HostSession(HostOptions opts)
        {
            o = opts;
            Files = new Transfers(p => SendPlain(p), () => link == null ? 0 : link.Queued, () => State == HostState.Connected, opts.DownloadDir ?? Transfers.DownloadFolder());
            Files.Note += s => Say(s);
        }
        void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }
        void Say(string s) { var h = Note; if (h != null) try { h(s); } catch { } }

        // connects to the relay and gets a code (on a background thread; watch Changed / Ended)
        public void Start()
        {
            new Thread(() =>
            {
                try
                {
                    link = new RelayLink();
                    link.Text += OnText; link.Binary += OnBinary; link.Closed += r => End(r, false);
                    link.Connect(o.Relay);
                    if (o.Unattended) link.SendText(Msg.ToJson(new Dictionary<string, object> { { "t", "host" }, { "persist", o.PersistSecret } }));
                    else link.SendText("{\"t\":\"host\"}");
                }
                catch (Exception e) { End("Couldn't reach the relay (" + Inner(e) + "). Check the relay address in Settings.", false); }
            }) { IsBackground = true, Name = "host start" }.Start();
        }
        static string Inner(Exception e) { while (e.InnerException != null) e = e.InnerException; return e.Message.TrimEnd('.'); }

        void OnText(string s)
        {
            var m = Msg.FromJson(s); var t = Msg.Str(m, "t");
            switch (t)
            {
                case "code":
                    Code = Msg.Str(m, "code");
                    if (o.Unattended) pwKey = o.UnattendedKey;
                    else { Password = Kdf.NewPassword(); pwKey = Kdf.PasswordKey(Password, Code); }
                    Crash.Secrets = new[] { Code, Password };
                    State = HostState.Waiting; Fire(); break;
                case "paired":
                    lock (gate) { State = HostState.Checking; ResetHandshake(); }
                    var myPair = handshakeId;
                    new Thread(() => { Thread.Sleep(30000); lock (gate) if (handshakeId == myPair && (State == HostState.Checking)) Kick("handshake timed out"); }) { IsBackground = true }.Start();
                    Fire(); break;
                case "peer-left":
                    if (State == HostState.Connected && !o.Unattended) { End("The helper left.", true); return; }
                    lock (gate) { if (State == HostState.Connected) Say("The helper left."); ResetHandshake(); State = HostState.Waiting; }
                    Fire(); break;
                case "expired": End("The code wasn't used for 10 minutes, so it stopped working. Click Get help again for a new one.", false); break;
                case "error": End("The relay said no (" + Msg.Str(m, "reason") + ").", false); break;
            }
        }
        int handshakeId;
        void ResetHandshake()
        {
            handshakeId++; consentId++;
            if (chan != null) { chan.Dispose(); chan = null; }
            if (ecdh != null) { ecdh.Dispose(); ecdh = null; }
            keys = null; viewerPub = null; Control = false; Clipboard = false; HelperName = null; Safety = null;
            ReleaseAll(); Files.CancelAll();
        }
        void Kick(string why)
        {
            ResetHandshake(); State = HostState.Waiting;
            if (link != null) link.SendText("{\"t\":\"kick\"}");
            Fire();
        }

        void OnBinary(byte[] b)
        {
            if (b.Length == 0) return;
            if (b[0] == Channel.Sealed) { OnSealed(b); return; }
            lock (gate)
            {
                if (State != HostState.Checking) { Kick("unexpected message"); return; }
                if (b[0] == Msg.VHello && b.Length == 67 && b[1] == Msg.Ver && viewerPub == null)
                {
                    viewerPub = Kdf.Slice(b, 2, 65);
                    ecdh = new EcdhKey();
                    var nonce = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(nonce);
                    try { keys = SessionKeys.Derive(ecdh.SharedHash(viewerPub), pwKey, Code, viewerPub, ecdh.Pub, nonce); }
                    catch (CryptographicException) { Kick("bad key"); return; }
                    link.SendBinary(Kdf.Cat(new[] { Msg.HHello, Msg.Ver }, ecdh.Pub, nonce));
                    return;
                }
                if (b[0] == Msg.VConfirm && b.Length == 33 && keys != null && chan == null)
                {
                    if (!Kdf.Equal(Kdf.Slice(b, 1, 32), keys.ConfirmV))
                    {
                        WrongPasswords++;
                        link.SendBinary(new[] { Msg.HReject });
                        Kick("wrong password");
                        var h = WrongPassword; if (h != null) try { h(); } catch { }
                        if (!o.Unattended && WrongPasswords >= 3) End("Someone typed the wrong password 3 times, so this code was closed. Click Get help again for a new one.", false);
                        else if (o.Unattended && WrongPasswords >= 5) End("Wrong unattended password 5 times - unattended access is paused for 15 minutes.", false);
                        return;
                    }
                    link.SendBinary(Kdf.Cat(new[] { Msg.HConfirm }, keys.ConfirmH));
                    chan = new Channel(keys, true); Safety = keys.Safety;
                    var hid = handshakeId;
                    new Thread(() => { Thread.Sleep(15000); lock (gate) if (handshakeId == hid && State == HostState.Checking) Kick("no hello"); }) { IsBackground = true }.Start();
                    return;
                }
                Kick("unexpected handshake message");
            }
        }

        void SendPlain(byte[] plain)
        {
            var c = chan; var l = link; if (c == null || l == null) return;
            l.SendSealed(() => c.Seal(plain), plain.Length + 25);
        }
        void SendJson(params object[] kv) { SendPlain(Msg.JsonMsg(kv)); }

        void OnSealed(byte[] b)
        {
            byte[] p;
            lock (gate)
            {
                if (chan == null) { Kick("sealed before the handshake"); return; }
                try { p = chan.Open(b); }
                catch (CryptographicException e) { Say("A message from the helper failed its check (" + e.Message + ") - disconnected to be safe."); if (o.Unattended) Kick(e.Message); else End("A message failed its security check, so the session was closed.", true); return; }
            }
            if (p.Length == 0) return;
            if (p[0] == Msg.Json) OnJson(Msg.ParseJson(p));
            else if (p[0] == Msg.Input) OnInput(p);
            else if (p[0] == Msg.File) { if (State == HostState.Connected) Files.Chunk(p); }
        }

        void OnJson(Dictionary<string, object> m)
        {
            var t = Msg.Str(m, "t");
            if (t == "hello")
            {
                lock (gate)
                {
                    if (State != HostState.Checking || HelperName != null) return;
                    var name = (Msg.Str(m, "name") ?? "").Trim(); var clean = new string(name.Where(ch => !char.IsControl(ch)).ToArray());
                    HelperName = clean.Length == 0 ? "Someone" : clean.Length > 40 ? clean.Substring(0, 40) : clean;
                    HelperClient = Msg.Str(m, "client") == "browser" ? "browser" : "app";
                    State = HostState.Asking;
                }
                Fire();
                if (o.Unattended) { Answer(consentId, new ConsentAnswer { Allow = true, Control = true, Clipboard = false }); return; }
                var id = consentId; int answered = 0;
                var h = ConsentNeeded;
                Action<ConsentAnswer> answer = a => { if (Interlocked.Exchange(ref answered, 1) == 0) Answer(id, a); };
                if (h == null) answer(new ConsentAnswer()); else h(HelperName, Safety, answer);
                // nobody answered in 60 s = no
                new Thread(() => { Thread.Sleep(60000); answer(new ConsentAnswer()); }) { IsBackground = true }.Start();
                return;
            }
            if (State != HostState.Connected) return;   // nothing else counts before Allow
            switch (t)
            {
                case "ack": OnAck((uint)Msg.Num(m, "seq")); break;
                case "monitor": { var i = (int)Msg.Num(m, "i"); if (i >= 0 && i < o.Screen.Monitors().Length) { monitor = i; keyframe = true; } break; }
                case "quality": { var q = Msg.Str(m, "q"); if (q == "auto" || q == "best" || q == "balanced" || q == "fast") { quality = q; var f = Quality.Fixed(q); if (f >= 0) SetLevel(f); } break; }
                case "refresh": keyframe = true; break;
                case "ping": SendJson("t", "pong", "ts", Msg.Num(m, "ts")); break;
                case "chat": { var text = Msg.Str(m, "text") ?? ""; if (text.Length > 2000) text = text.Substring(0, 2000); var h = Chat; if (h != null) h(text); break; }
                case "clip": if (Clipboard && o.Clip != null) { var text = Msg.Str(m, "text") ?? ""; if (text.Length <= 1000000) { lastClip = text; o.Clip.Set(text); Say("The helper put text on your clipboard."); } } break;
                case "bye": End(Msg.Str(m, "reason") ?? "The helper ended the session.", false); break;
                // v1.2: drawing is allowed in view-only sessions too - it only shows lines, it can't click anything
                case "draw":
                    {
                        var pts = Draw.Points(m); if (pts == null) break;
                        var mons = o.Screen.Monitors(); if (mons.Length == 0) break;
                        var mi = (int)Msg.Num(m, "mon"); if (mi < 0 || mi >= mons.Length) mi = monitor;
                        var h = Drawing; if (h != null) h(mons[Math.Min(mi, mons.Length - 1)], Msg.Num(m, "id"), pts);
                        break;
                    }
                case "draw-clear": { var h = DrawClear; if (h != null) h(); break; }
                // v1.2: only your own PCs (unattended access) can be restarted from the helper's side
                case "restart":
                    if (!o.Unattended) { SendJson("t", "note", "text", "Restarting is only for PCs with unattended access."); break; }
                    { SendJson("t", "restarting"); Say((HelperName ?? "The helper") + " is restarting this PC."); var h = RestartRequested; if (h != null) h(HelperName); }
                    break;
                default: Files.Handle(m); break;
            }
        }

        void Answer(int id, ConsentAnswer a)
        {
            lock (gate)
            {
                if (id != consentId || State != HostState.Asking) return;
                if (!a.Allow)
                {
                    SendJson("t", "consent", "allowed", false);
                    if (o.Unattended) { Kick("denied"); return; }
                    State = HostState.Ended;
                }
                else
                {
                    Control = a.Control; Clipboard = a.Clipboard; State = HostState.Connected;
                    WrongPasswords = 0;   // v1.1: only wrong tries in a row count towards the unattended pause
                    var mons = o.Screen.Monitors();
                    SendJson("t", "consent", "allowed", true, "control", Control, "clipboard", Clipboard, "unattended", o.Unattended,
                        "monitors", mons.Select((mm, i) => new Dictionary<string, object> { { "i", i }, { "w", mm.W }, { "h", mm.H }, { "primary", mm.Primary } }).ToArray());
                    monitor = Math.Max(0, Array.FindIndex(mons, mm => mm.Primary));
                    keyframe = true; inflight.Clear();
                    var cid = consentId;
                    new Thread(() => CaptureLoop(cid)) { IsBackground = true, Name = "capture", Priority = ThreadPriority.BelowNormal }.Start();
                }
            }
            if (!a.Allow && !o.Unattended) { End("You said no, so nothing was shared.", false); return; }
            Fire();
        }

        // ---- while connected: the person can stop control or clipboard sharing at any time
        public void SetControl(bool on)
        {
            if (State != HostState.Connected) return;
            Control = on; if (!on) ReleaseAll();
            SendJson("t", "control", "on", on); Fire();
        }
        public void SetClipboard(bool on)
        {
            if (State != HostState.Connected) return;
            Clipboard = on; lastClip = null; SendJson("t", "clipboard", "on", on); Fire();
        }
        public void SendChat(string text) { if (State == HostState.Connected && !string.IsNullOrWhiteSpace(text)) SendJson("t", "chat", "text", text.Length > 2000 ? text.Substring(0, 2000) : text); }
        public string SendFile(string path) { if (State != HostState.Connected) return "Not connected."; return Files.Offer(path); }
        // Disconnect button, panic hotkey, closing the app
        public void Disconnect(string why)
        {
            if (o.Unattended && State == HostState.Connected) { SendJson("t", "bye", "reason", "They disconnected you."); lock (gate) Kick("disconnect"); Say(why); return; }
            End(why, true);
        }

        public void End(string reason, bool tellPeer)
        {
            if (Interlocked.Exchange(ref ended, 1) == 1) return;
            if (tellPeer && chan != null) SendJson("t", "bye", "reason", "They ended the session.");
            lock (gate) { State = HostState.Ended; EndReason = reason; ReleaseAll(); Files.CancelAll(); }
            Crash.Secrets = new string[0];
            Password = null;
            if (link != null) link.Close(reason);
            Fire();
            var h = Ended; if (h != null) try { h(reason); } catch { }
        }

        // ---- input (only while connected and control is allowed). Held keys/buttons are let go when control stops.
        readonly HashSet<int> keysDown = new HashSet<int>(); readonly HashSet<int> buttonsDown = new HashSet<int>();
        public long InputEvents;
        void OnInput(byte[] p)
        {
            if (State != HostState.Connected || !Control || p.Length < 2) return;
            var mons = o.Screen.Monitors(); var mon = mons[Math.Min(monitor, mons.Length - 1)];
            int px, py; InputEvents++;
            try
            {
                switch (p[1])
                {
                    case Msg.InMove: if (p.Length < 6) return; InputMap.ToPixel(mon, Msg.RU16(p, 2), Msg.RU16(p, 4), out px, out py); o.Input.Move(px, py); break;
                    case Msg.InButton:
                        {
                            if (p.Length < 8 || p[2] > 2) return;
                            InputMap.ToPixel(mon, Msg.RU16(p, 4), Msg.RU16(p, 6), out px, out py); o.Input.Move(px, py);
                            bool down = p[3] != 0;
                            lock (buttonsDown) { if (down) buttonsDown.Add(p[2]); else if (!buttonsDown.Remove(p[2])) return; }
                            o.Input.Button(p[2], down); break;
                        }
                    case Msg.InWheel: if (p.Length < 6) return; o.Input.Wheel(Clamp(Msg.RI16(p, 2)), Clamp(Msg.RI16(p, 4))); break;
                    case Msg.InKey:
                        {
                            if (p.Length < 5) return;
                            int scan = Msg.RU16(p, 2); bool down = p[4] != 0;
                            if ((scan & 0xFF00) != 0 && (scan & 0xFF00) != 0xE000) return;
                            lock (keysDown) { if (down) keysDown.Add(scan); else keysDown.Remove(scan); }
                            o.Input.Key(scan, down); break;
                        }
                    case Msg.InText: o.Input.Text(System.Text.Encoding.UTF8.GetString(p, 2, p.Length - 2)); break;
                }
            }
            catch { }
        }
        static int Clamp(int v) { return Math.Max(-1200, Math.Min(1200, v)); }
        void ReleaseAll()
        {
            if (o.Input == null) return;
            int[] k, b; lock (keysDown) { k = keysDown.ToArray(); keysDown.Clear(); } lock (buttonsDown) { b = buttonsDown.ToArray(); buttonsDown.Clear(); }
            foreach (var s in k) try { o.Input.Key(s, false); } catch { }
            foreach (var s in b) try { o.Input.Button(s, false); } catch { }
        }

        // ---- the picture: changed tiles, at most a few frames waiting for the viewer's ack; quality follows the speed
        volatile int monitor; volatile bool keyframe; volatile string quality = "auto";
        int level = 1; DateTime levelChanged = DateTime.MinValue; double ackMs = 100;
        readonly Dictionary<uint, DateTime> inflight = new Dictionary<uint, DateTime>();
        string lastClip; bool lastSecure, lastElevated;
        public string ScreenKind { get { return o.Screen.Kind; } }
        public int Level { get { return level; } }
        void OnAck(uint seq)
        {
            lock (inflight)
            {
                DateTime t; if (!inflight.TryGetValue(seq, out t)) return;
                foreach (var k in inflight.Keys.Where(k => k <= seq).ToList()) inflight.Remove(k);
                ackMs = ackMs * 0.7 + (DateTime.UtcNow - t).TotalMilliseconds * 0.3;
            }
            if (quality != "auto") return;
            var since = (DateTime.UtcNow - levelChanged).TotalMilliseconds;
            if (ackMs > 350 && level < Quality.Jpeg.Length - 1 && since > 1500) SetLevel(level + 1);
            else if (ackMs < 120 && level > 0 && since > 4000) SetLevel(level - 1);
        }
        void SetLevel(int l)
        {
            if (l == level) return;
            if (Quality.Scale[l] != Quality.Scale[level]) keyframe = true;
            level = l; levelChanged = DateTime.UtcNow;
            SendJson("t", "q", "label", Quality.Label[l]);
        }
        void CaptureLoop(int cid)
        {
            var enc = new TileEncoder(); uint seq = 0; var sw = Stopwatch.StartNew(); long lastFrame = -1000, lastCheck = -1000, lastClipCheck = 0;
            int f = Quality.Fixed(quality); if (f >= 0) level = f;
            try
            {
                while (State == HostState.Connected && consentId == cid)
                {
                    long now = sw.ElapsedMilliseconds;
                    if (now - lastCheck >= 1000)
                    {
                        lastCheck = now;
                        bool secure = o.Screen.SecureDesktop || (!AssistSettings.Fake && Desktop.SecureDesktopActive());
                        bool elevated = !secure && !AssistSettings.Fake && Desktop.ForegroundElevated();
                        if (secure != lastSecure || elevated != lastElevated) { lastSecure = secure; lastElevated = elevated; SendJson("t", "state", "secure", secure, "elevated", elevated); Fire(); }
                    }
                    if (Clipboard && o.Clip != null && now - lastClipCheck >= 1000)
                    {
                        lastClipCheck = now;
                        var c = o.Clip.Get();
                        if (c != null && c != lastClip && c.Length <= 1000000) { bool first = lastClip == null; lastClip = c; if (!first) SendJson("t", "clip", "text", c); }
                    }
                    int interval = quality == "fast" ? 50 : 66;
                    int waiting; lock (inflight) waiting = inflight.Count;
                    if (now - lastFrame < interval || waiting >= 3 || link.Queued > 1024 * 1024) { Thread.Sleep(8); continue; }
                    lastFrame = now;
                    bool key = keyframe; keyframe = false;
                    List<Rectangle> dirty;
                    bool changed = o.Screen.Capture(monitor, Quality.Scale[level], out dirty);
                    if (!changed && !key) continue;
                    if (key) enc.Reset();
                    var tiles = enc.Encode(o.Screen.Frame, o.Screen.FrameW, o.Screen.FrameH, Quality.Jpeg[level], key, key ? null : dirty);
                    if (tiles.Count == 0) continue;
                    var mons = o.Screen.Monitors(); var m = mons[Math.Min(monitor, mons.Length - 1)];
                    // big updates go out in parts of at most ~1 MB so one message never hits the relay's size limit
                    var batch = new List<Tile>(); long size = 0;
                    for (int i = 0; i <= tiles.Count; i++)
                    {
                        if (i == tiles.Count || (size + tiles[i].Jpeg.Length > 1000000 && batch.Count > 0))
                        {
                            var fr = new FrameMsg { Mon = monitor, SrcW = m.W, SrcH = m.H, EncW = o.Screen.FrameW, EncH = o.Screen.FrameH, Seq = ++seq, Flags = key ? 1 : 0, Tiles = batch };
                            var bytes = fr.Encode();
                            lock (inflight) inflight[seq] = DateTime.UtcNow;
                            SendPlain(bytes); FramesSent++; BytesSent += bytes.Length;
                            batch = new List<Tile>(); size = 0; key = false;
                        }
                        if (i < tiles.Count) { batch.Add(tiles[i]); size += tiles[i].Jpeg.Length; }
                    }
                    // a viewer that stops answering for 20 s is gone
                    lock (inflight) if (inflight.Count > 0 && (DateTime.UtcNow - inflight.Values.Min()).TotalSeconds > 20) { inflight.Clear(); keyframe = true; }
                }
            }
            catch (Exception e) { Say("Screen capture stopped: " + e.Message); End("Screen capture failed (" + e.Message + ").", true); }
        }
    }
}

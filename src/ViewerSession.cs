// Hallow Assist "Help someone": the helper's side. Enter their code and password -> the key agreement checks the password
// (the relay can't), both sides show the same 4-digit safety code, then the other person decides Allow / Deny.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Security.Cryptography;
using System.Threading;

namespace HallowAssist
{
    public enum ViewerState { Connecting, Checking, Waiting, Connected, Ended }

    public class ViewerSession
    {
        readonly Uri relay; readonly string code, password, name; readonly byte[] presetKey;
        RelayLink link; Channel chan; EcdhKey ecdh; SessionKeys keys; byte[] pwKey;
        public volatile ViewerState State = ViewerState.Connecting;
        public string Safety, HostName, EndReason, Status = "Connecting to the relay...", Banner = "", QualityLabel = "";
        public bool ControlAllowed, ClipboardAllowed;
        public bool HostUnattended, Restarting;   // v1.2: one of your own PCs (it can be restarted); a restart was started
        public MonitorInfo[] Monitors = new MonitorInfo[0];
        public int Monitor;
        public readonly FrameBuffer Screen = new FrameBuffer();
        public readonly object ScreenLock = new object();
        public long Frames, FrameBytes; public int RttMs;
        public Transfers Files;
        public IClip Clip; public volatile bool ClipSync; string lastClip;
        public IFileClip FileClip;   // v1.5: copied files
        public bool FromKeys;        // v1.5: the key was made from a password Hallow Keys gave (one of Your PCs)
        int ended;

        public event Action Changed;
        public event Action<Rectangle> Painted;   // an area of Screen changed (on the receive thread)
        public event Action<string> Chat;
        public event Action<string> Note;
        public event Action<string> Ended;
        public event Action<string> Detail;   // v1.4: connection details (the log and the stats tooltip), not a status line
        public string PathNote = "";
        void Tell(string s) { PathNote = s; var h = Detail; if (h != null) try { h(s); } catch { } }
        // v1.4: Wake-on-LAN (one of your own PCs told us its network adapter) and session sound
        public string HostMac;
        public bool HostSharesSound, HostHasSound; public volatile bool SoundOn; public IAudioSink Player; public long AudioPackets;

        // v1.1: one of Your PCs with its remembered key (the password itself is never kept)
        public ViewerSession(Uri relay, string code, byte[] passwordKey, string name) : this(relay, code, (string)null, name) { presetKey = passwordKey; }
        public string Code { get { return code; } }
        public byte[] PasswordKey { get { return pwKey; } }
        public bool UsedSavedKey { get { return presetKey != null; } }
        public bool OneTimePassword { get { return presetKey == null && Kdf.IsOneTime(password); } }
        public bool WrongPassword { get { return rejected; } }
        public ViewerSession(Uri relay, string code, string password, string name)
        {
            this.relay = relay; this.code = Kdf.NormCode(code); this.password = password; this.name = string.IsNullOrWhiteSpace(name) ? "Helper" : name.Trim();
            Files = new Transfers(p => SendPlain(p), () => { var t = tx; return t != null ? t.Queued : link == null ? 0 : link.Queued; }, () => State == ViewerState.Connected, Transfers.DownloadFolder());
            Files.Note += s => Say(s);
            // v1.5: copied files that arrived go on this PC's clipboard too (only while they share their clipboard)
            Files.BatchReceived += (paths, clip) =>
            {
                if (!clip || !ClipboardAllowed || FileClip == null || State != ViewerState.Connected) return;
                FileClip.SetFiles(paths);
                Say((paths.Length == 1 ? "The file is" : "The " + paths.Length + " files are") + " on your clipboard too - paste with Ctrl+V into any folder.");
            };
        }
        void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }
        void Say(string s) { var h = Note; if (h != null) try { h(s); } catch { } }

        public void Start()
        {
            new Thread(() =>
            {
                try
                {
                    var pw = new Thread(() => { pwKey = presetKey ?? Kdf.PasswordKey(password, code); }); pw.Start();
                    ecdh = new EcdhKey();
                    link = new RelayLink();
                    link.Text += OnText; link.Binary += OnBinary; link.Closed += r => End(State == ViewerState.Connecting ? "Couldn't reach the relay. Check the relay address in Settings." : r, false);
                    link.Connect(relay);
                    pw.Join();
                    link.SendText(Msg.ToJson(new Dictionary<string, object> { { "t", "join" }, { "code", code } }));
                    new Thread(PingLoop) { IsBackground = true }.Start();
                }
                catch (Exception e) { var x = e; while (x.InnerException != null) x = x.InnerException; End("Couldn't reach the relay (" + x.Message.TrimEnd('.') + "). Check the relay address in Settings.", false); }
            }) { IsBackground = true, Name = "viewer start" }.Start();
        }
        void PingLoop()
        {
            while (State != ViewerState.Ended)
            {
                Thread.Sleep(1000);
                if (State != ViewerState.Connected) continue;
                CheckRecording(); CheckPull();
                if (Environment.TickCount % 2000 < 1000) SendJson("t", "ping", "ts", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds);
                if (ClipSync && ClipboardAllowed && Clip != null)
                {
                    var c = Clip.Get();
                    if (c != null && c != lastClip && c.Length <= 1000000) { bool first = lastClip == null; lastClip = c; if (!first) SendJson("t", "clip", "text", c); }
                }
            }
        }

        void OnText(string s)
        {
            var m = Msg.FromJson(s);
            switch (Msg.Str(m, "t"))
            {
                case "paired":
                    State = ViewerState.Checking; Status = "Found them. Checking the password...";
                    link.SendBinary(Kdf.Cat(new[] { Msg.VHello, Msg.Ver }, ecdh.Pub)); Fire(); break;
                case "error":
                    var r = Msg.Str(m, "reason");
                    End(r == "not-found" ? "That code isn't open. Check the 9 digits, or ask them for a new code." : r == "busy" ? "Someone is already connected to that code." :
                        r == "rate-limited" ? "Too many tries from here. Wait " + Math.Max(1, (Msg.Num(m, "retryAfter") + 59) / 60) + " minutes." : "The relay said no (" + r + ").", false); break;
                case "peer-left": End("They closed the session.", false); break;
                case "kicked": End(rejected ? "Wrong password." : "They ended the session.", false); break;
            }
        }
        bool rejected;
        void OnBinary(byte[] b)
        {
            if (b.Length == 0) return;
            if (b[0] == Channel.Sealed && chan != null) { OnSealed(b); return; }
            if (State != ViewerState.Checking) { End("Unexpected message - closed to be safe.", false); return; }
            if (b[0] == Msg.HHello && b.Length == 83 && keys == null)
            {
                var hostPub = Kdf.Slice(b, 2, 65); var nonce = Kdf.Slice(b, 67, 16);
                try { keys = SessionKeys.Derive(ecdh.SharedHash(hostPub), pwKey, code, ecdh.Pub, hostPub, nonce); }
                catch (CryptographicException) { End("Their PC sent a bad key - closed to be safe.", false); return; }
                link.SendBinary(Kdf.Cat(new[] { Msg.VConfirm }, keys.ConfirmV));
                return;
            }
            if (b[0] == Msg.HReject) { rejected = true; End("Wrong password.", false); return; }
            if (b[0] == Msg.HConfirm && b.Length == 33 && keys != null && chan == null)
            {
                if (!Kdf.Equal(Kdf.Slice(b, 1, 32), keys.ConfirmH)) { End("Couldn't verify their PC - the connection may not be safe.", false); return; }
                chan = new Channel(keys, false); Safety = keys.Safety; State = ViewerState.Waiting;
                var tr = new Transport(link); tr.Deliver = OnSealed; tr.Overflow = r => DirectFailed(r); tx = tr;
                Status = "Connected securely. Waiting for them to click Allow...";
                SendJson("t", "hello", "name", name.Length > 40 ? name.Substring(0, 40) : name, "client", "app", "version", Program.Version, "files2", true);
                Fire(); return;
            }
            End("Unexpected message - closed to be safe.", false);
        }
        void SendPlain(byte[] plain) { var c = chan; var t = tx; if (c == null || t == null || State == ViewerState.Ended) return; t.SendSealed(() => c.Seal(plain), plain.Length + 25); }

        // ---- v1.3: direct connection on the same network. This side listens (the helper's PC may ask once whether Hallow
        // Assist may use private networks); their PC connects out and proves itself with the token sent in the sealed channel.
        Transport tx; DirectLink direct; int directOffered; bool peerFellBack;
        public bool AllowDirect = true;
        public bool Direct { get { var t = tx; return t != null && t.IsDirect; } }
        void OfferDirect()
        {
            if (!AllowDirect || Interlocked.Exchange(ref directOffered, 1) == 1) return;
            var c = chan; var t = tx; if (c == null || t == null) return;
            new Thread(() =>
            {
                using (var l = new DirectListener())
                {
                    if (l.Endpoints.Count == 0) return;   // not on a home network: the relay it is
                    SendJson("t", "direct-offer", "addrs", l.Endpoints.ToArray(), "token", Convert.ToBase64String(l.Token));
                    var d = l.Accept(12000);
                    if (d == null) { Tell("Their PC couldn't reach this one directly (another network, or a firewall) - staying on the relay."); return; }
                    if (State != ViewerState.Connected) { d.Close("too late"); return; }
                    d.Frame += t.OnDirectFrame;
                    d.Closed += r => { if (direct == d) DirectFailed(r); };
                    direct = d; d.Start();
                    if (t.SwitchTo(d, () => c.Seal(Msg.JsonMsg("t", "switch")))) { Tell("Connected directly over your network."); Say("Connected directly over your network."); Fire(); }
                    else { direct = null; d.Close("switch didn't happen"); Tell("The switch to a direct connection didn't finish - staying on the relay."); }
                }
            }) { IsBackground = true, Name = "direct listen" }.Start();
        }
        // v1.4: a direct link that drops mid-session goes back to the relay when the relay is still connected
        public void CutDirect(string why) { var d = direct; if (d != null) d.Close(why); }   // tests: the direct link drops
        void DirectFailed(string why)
        {
            var d = direct; direct = null; if (d != null) d.Close(why);
            if (State == ViewerState.Ended) return;
            var t = tx; var c = chan; var l = link;
            if (t != null && t.FellBack) return;
            if (t != null && c != null && l != null && l.IsOpen)
            {
                t.Fallback(() => c.Seal(Transport.MarkerPlain()));
                Tell("The direct connection dropped (" + why + ") - back on the relay."); Say("Back on the relay - the direct connection dropped.");
                SendJson("t", "refresh");
                Fire(); return;
            }
            End("The direct connection dropped (" + why + "). Connect again.", false);
        }
        void SendJson(params object[] kv) { SendPlain(Msg.JsonMsg(kv)); }

        void OnSealed(byte[] b)
        {
            byte[] p;
            try { p = chan.Open(b, directOffered == 1 && !peerFellBack ? (Func<byte[], bool>)Transport.IsMarker : null); }
            catch (CryptographicException e) { End("A message failed its security check (" + e.Message + "), so the session was closed.", false); return; }
            if (p.Length == 0) return;
            if (p[0] == Msg.Frame) { OnFrame(p); return; }
            if (p[0] == Msg.File) { Files.Chunk(p); return; }
            if (p[0] == Msg.Audio) { OnAudio(p); return; }
            if (p[0] != Msg.Json) return;
            var m = Msg.ParseJson(p);
            switch (Msg.Str(m, "t"))
            {
                case "consent":
                    if (!Msg.Bool(m, "allowed")) { End("They said no.", false); return; }
                    ControlAllowed = Msg.Bool(m, "control"); ClipboardAllowed = Msg.Bool(m, "clipboard"); HostName = Msg.Str(m, "host"); HostUnattended = Msg.Bool(m, "unattended");
                    HostSharesSound = Msg.Bool(m, "sound"); HostHasSound = Msg.Bool(m, "soundAvailable");
                    Files.PeerBatches = Msg.Bool(m, "files2"); HostCanRecord = Msg.Bool(m, "rec");   // v1.5
                    HostAsksFiles = Msg.Bool(m, "askFiles");   // v1.6
                    var list = new List<MonitorInfo>();
                    object mo; if (m.TryGetValue("monitors", out mo) && mo is System.Collections.ArrayList)
                        foreach (var x in (System.Collections.ArrayList)mo) { var d = x as Dictionary<string, object>; if (d != null) list.Add(new MonitorInfo { W = (int)Msg.Num(d, "w"), H = (int)Msg.Num(d, "h"), Primary = Msg.Bool(d, "primary") }); }
                    Monitors = list.ToArray();
                    State = ViewerState.Connected; Status = "Connected."; Banner = ControlAllowed ? "" : "View only - they haven't allowed control.";
                    OfferDirect();
                    if (SoundOn) SendJson("t", "sound-want", "on", true);
                    Fire(); break;
                case "control": ControlAllowed = Msg.Bool(m, "on"); Banner = ControlAllowed ? "" : "They turned off control - you can still see their screen."; Fire(); break;
                case "clipboard": ClipboardAllowed = Msg.Bool(m, "on"); lastClip = null; if (!ClipboardAllowed) PullWaiting = false; Fire(); break;
                // v1.6: their answer to "Copy their files here" (Allow / Deny / no answer in 60 s / can't)
                case "clip-files-answer":
                    {
                        if (!PullWaiting) break;
                        PullWaiting = false; var allowed = Msg.Bool(m, "allowed");
                        PullLast = allowed ? (Msg.Str(m, "reason") == "auto" ? "auto" : "allowed") : Msg.Str(m, "reason") ?? "deny";   // v1.7: "auto" = an unattended PC allowed it by itself
                        Say(FilePull.AnswerText(allowed, Msg.Str(m, "reason"), Msg.Str(m, "text")));
                        Fire(); break;
                    }
                case "clip": { var t = Msg.Str(m, "text") ?? ""; if (ClipboardAllowed && Clip != null && ClipSync) { lastClip = t; Clip.Set(t); } LastClipFromThem = t; Fire(); break; }
                case "state":
                    Banner = Msg.Bool(m, "secure") ? "They need to click the admin prompt themselves - Windows hides it from remote help." :
                        Msg.Bool(m, "elevated") ? "That window runs as administrator: you can see it, but they need to click it themselves." : (ControlAllowed ? "" : "View only - they haven't allowed control.");
                    Fire(); break;
                case "chat": { var h = Chat; var t = Msg.Str(m, "text") ?? ""; if (h != null) h(t.Length > 2000 ? t.Substring(0, 2000) : t); break; }
                case "pong": RttMs = (int)Math.Max(0, (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds - Msg.Num(m, "ts")); break;
                case "q": QualityLabel = Msg.Str(m, "label") ?? ""; Fire(); break;
                case "bye": End(Msg.Str(m, "reason") ?? "They ended the session.", false); break;
                case "switch": { var sx = tx; if (sx != null) sx.PeerSwitched(); break; }
                case "relay-again": { peerFellBack = true; var sx = tx; if (sx != null) sx.PeerFellBack(); if (direct != null) DirectFailed("their PC went back to the relay"); break; }
                // v1.4: only one of your own PCs (unattended access) sends this - kept for Wake-on-LAN
                case "mac": if (HostUnattended) { var mac = Wol.NormMac(Msg.Str(m, "mac")); if (mac != null) { HostMac = mac; Fire(); } } break;
                case "sound": HostSharesSound = Msg.Bool(m, "on"); if (!HostSharesSound) { var pl = Player; if (pl != null) pl.Stop(); } Fire(); break;
                case "restarting": Restarting = true; Status = "Their PC is restarting - Hallow Assist reconnects when it's back."; Fire(); break;
                case "note": { var t = Msg.Str(m, "text"); if (!string.IsNullOrEmpty(t)) Say(t); break; }
                // v1.5: their red bar shows "Recording" (first time: recording may start; then every few seconds: it still does)
                case "rec-ok": OnRecOk(); break;
                case "rec": if (!Msg.Bool(m, "on") && Rec != RecState.Off) StopRecording("They stopped the recording.", false); break;
                default: Files.Handle(m); break;
            }
        }
        public string LastClipFromThem;

        // ---- v1.4: sound (off until you turn it on; only plays while they share it)
        public void SetSound(bool on)
        {
            SoundOn = on;
            if (!on) { var pl = Player; if (pl != null) pl.Stop(); }
            if (State == ViewerState.Connected) SendJson("t", "sound-want", "on", on);
            Fire();
        }
        void OnAudio(byte[] p)
        {
            if (!SoundOn || !HostSharesSound || State != ViewerState.Connected) return;
            AudioMsg a; try { a = AudioMsg.Parse(p); } catch { return; }
            if (a.SampleRate != AudioMsg.Rate) return;
            AudioPackets++;
            var pcm = a.ToPcm();
            var pl = Player; if (pl != null) try { pl.Play(pcm); } catch { }
            var r = recorder; if (r != null) try { r.AddAudio(pcm); } catch { }
        }

        void OnFrame(byte[] p)
        {
            FrameMsg f;
            try { f = FrameMsg.Parse(p); } catch { return; }
            var area = Rectangle.Empty;
            lock (ScreenLock)
            {
                if (f.EncW != Screen.W || f.EncH != Screen.H) { Screen.Resize(f.EncW, f.EncH); area = new Rectangle(0, 0, f.EncW, f.EncH); }
                Monitor = f.Mon;
                foreach (var t in f.Tiles) { try { var r = Screen.Apply(t); area = area.IsEmpty ? r : Rectangle.Union(area, r); } catch { } }
            }
            FrameBytes += p.Length; Frames++;
            SendJson("t", "ack", "seq", f.Seq);
            var h = Painted; if (h != null && !area.IsEmpty) h(area);
        }

        // ---- the helper's actions
        public bool CanControl { get { return State == ViewerState.Connected && ControlAllowed; } }
        public void Move(int nx, int ny) { if (CanControl) SendPlain(Msg.Move(nx, ny)); }
        public void Button(int b, bool down, int nx, int ny) { if (CanControl) SendPlain(Msg.Button(b, down, nx, ny)); }
        public void Wheel(int dy, int dx) { if (CanControl) SendPlain(Msg.Wheel(dy, dx)); }
        public void Key(int scan, bool down) { if (CanControl) SendPlain(Msg.Key(scan, down)); }
        public void Text(string s) { if (CanControl && !string.IsNullOrEmpty(s)) SendPlain(Msg.Text(s)); }
        // v1.2: drawing on their screen (0..65535 across the monitor you're looking at) and restarting your own PC
        public void SendDraw(long id, int[] pts) { if (State == ViewerState.Connected && pts != null && pts.Length >= 2) SendJson("t", "draw", "id", id, "mon", Monitor, "pts", pts); }
        public void ClearDraw() { if (State == ViewerState.Connected) SendJson("t", "draw-clear"); }
        public bool CanRestart { get { return State == ViewerState.Connected && HostUnattended && !Restarting; } }
        public void RequestRestart() { if (CanRestart) SendJson("t", "restart"); }
        public string HelperName { get { return name; } }
        public void SetMonitor(int i) { if (State == ViewerState.Connected) SendJson("t", "monitor", "i", i); }
        public void SetQuality(string q) { if (State == ViewerState.Connected) SendJson("t", "quality", "q", q); }
        public void SendChat(string t) { if (State == ViewerState.Connected && !string.IsNullOrWhiteSpace(t)) SendJson("t", "chat", "text", t); }
        public void SendClip(string t) { if (State == ViewerState.Connected && ClipboardAllowed && t != null) SendJson("t", "clip", "text", t); }
        public string SendFile(string path) { if (State != ViewerState.Connected) return "Not connected."; return Files.Offer(path); }
        // v1.5: copied files, both ways (only while they share their clipboard; the receiving side is asked first)
        public string SendCopiedFiles()
        {
            if (State != ViewerState.Connected) return "Not connected.";
            if (!ClipboardAllowed) return "They haven't turned on clipboard sharing.";
            if (FileClip == null) return "Copied files aren't available here.";
            return Files.OfferMany(FileClip.GetFiles(), true);
        }
        public string GetCopiedFiles()
        {
            if (State != ViewerState.Connected) return "Not connected.";
            if (!ClipboardAllowed) return "They haven't turned on clipboard sharing.";
            // v1.6: their PC must ask them first - a 1.5 PC would send without asking, so it isn't asked at all
            if (!HostAsksFiles) return FilePull.OldPeerText;
            if (PullWaiting) return FilePull.WaitingText;
            PullWaiting = true; PullAskedAt = DateTime.UtcNow; PullLast = null;
            SendJson("t", "clip-files-ask"); Fire();
            return FilePull.WaitingText;
        }
        public bool HostAsksFiles; public volatile bool PullWaiting; public string PullLast; DateTime PullAskedAt;   // v1.6
        void CheckPull()
        {
            // their PC answers within 60 s (then it counts as no); if nothing comes at all, stop waiting a little later
            if (PullWaiting && (DateTime.UtcNow - PullAskedAt).TotalMilliseconds > FilePull.TimeoutMs + 15000) { PullWaiting = false; PullLast = "timeout"; Say("They said no - no answer came."); Fire(); }
        }

        // ---- v1.5: session recording (this side only, off until you click Record). It starts only after their PC confirms
        // that its red bar says "Recording", and stops when they press Stop recording, the confirmations stop, or the session ends.
        public enum RecState { Off, Asking, On }
        public volatile RecState Rec = RecState.Off; public bool HostCanRecord;
        SessionRecorder recorder; DateTime recAsked, recLastOk; public DateTime RecStarted;
        public string RecFolder = SessionRecorder.DefaultFolder(), RecLastPath, RecLastKind, RecLabel; public long RecFirstFrameAt, RecFrames;
        public event Action<string> RecordingSaved;   // full path
        readonly object recGate = new object();
        public static int RecAskMs { get { return AssistSettings.TestDir != null ? 3000 : 10000; } }
        public static int RecStaleMs { get { return AssistSettings.TestDir != null ? 4000 : 10000; } }
        public string StartRecording()
        {
            if (State != ViewerState.Connected) return "Not connected.";
            lock (recGate)
            {
                if (Rec != RecState.Off) return null;
                Rec = RecState.Asking; recAsked = DateTime.UtcNow;
            }
            SendJson("t", "rec", "on", true);
            Say("Asking their PC to show \u201cRecording\u201d on its red bar - recording starts once it does.");
            Fire(); return null;
        }
        void OnRecOk()
        {
            SessionRecorder r = null;
            lock (recGate)
            {
                if (Rec == RecState.On) { recLastOk = DateTime.UtcNow; return; }
                if (Rec != RecState.Asking || State != ViewerState.Connected) return;
                r = new SessionRecorder(RecFolder, RecLabel ?? HostName ?? Ui.FormatCode(code), SessionRecorder.FindFfmpeg(), ver =>
                {
                    if (Frames == ver) return null;
                    lock (ScreenLock)
                    {
                        if (Screen.W == 0) return null;
                        return new RecFrame { Pixels = (byte[])Screen.Pixels.Clone(), W = Screen.W, H = Screen.H, Version = Frames };
                    }
                });
                var err = r.Start();
                if (err != null) { Rec = RecState.Off; r = null; SendJson("t", "rec", "on", false); Say("Couldn't record: " + err); Fire(); return; }
                recorder = r; Rec = RecState.On; recLastOk = DateTime.UtcNow; RecStarted = DateTime.Now; RecFirstFrameAt = 0; RecFrames = 0;
            }
            Say("Recording - their red bar says so. It's saved in " + RecFolder + " on this PC.");
            Fire();
        }
        void CheckRecording()
        {
            var r = recorder;
            if (r != null) { RecFrames = r.FramesWritten; if (RecFirstFrameAt == 0 && r.Started) RecFirstFrameAt = (long)(r.FirstFrameAt - new DateTime(1970, 1, 1)).TotalMilliseconds; }
            if (Rec == RecState.Asking && (DateTime.UtcNow - recAsked).TotalMilliseconds > RecAskMs)
            {
                lock (recGate) { if (Rec != RecState.Asking) return; Rec = RecState.Off; }
                SendJson("t", "rec", "on", false);
                Say(HostCanRecord ? "Their PC didn't confirm that its red bar says Recording, so nothing was recorded." : "Their Hallow Assist is older than 1.5 and can't show that you're recording, so nothing was recorded.");
                Fire(); return;
            }
            if (Rec == RecState.On && (DateTime.UtcNow - recLastOk).TotalMilliseconds > RecStaleMs) { StopRecording("Stopped recording: their PC stopped confirming that its red bar says Recording.", true); return; }
            if (Rec == RecState.On && r != null && !r.Running) StopRecording(r.Error ?? "The recording stopped.", true);
            if (Rec == RecState.On) Fire();   // the elapsed time on the button
        }
        // stops and finishes the file in the background (FFmpeg can take a few seconds)
        public void StopRecording(string why, bool tellPeer)
        {
            SessionRecorder r;
            lock (recGate)
            {
                if (Rec == RecState.Off) return;
                var was = Rec; Rec = RecState.Off; r = recorder; recorder = null;
                if (was == RecState.Asking) r = null;
            }
            if (tellPeer && State == ViewerState.Connected) SendJson("t", "rec", "on", false);
            Fire();
            if (r == null) { if (why != null) Say(why); return; }
            new Thread(() =>
            {
                string path = null; try { path = r.Stop(); } catch (Exception e) { r.Error = e.Message; }
                RecFrames = r.FramesWritten; if (RecFirstFrameAt == 0 && r.Started) RecFirstFrameAt = (long)(r.FirstFrameAt - new DateTime(1970, 1, 1)).TotalMilliseconds;
                RecLastKind = r.Kind; RecLastPath = path;
                Say((why ?? "Recording stopped.") + (path != null ? " Saved " + System.IO.Path.GetFileName(path) + " in " + System.IO.Path.GetDirectoryName(path) + "." : " Nothing was recorded.") + (r.Error != null && why != r.Error ? " " + r.Error : ""));
                var h = RecordingSaved; if (h != null && path != null) try { h(path); } catch { }
                Fire();
            }) { IsBackground = true, Name = "recording finish" }.Start();
        }

        public void End(string reason, bool tellPeer)
        {
            if (Interlocked.Exchange(ref ended, 1) == 1) return;
            if (tellPeer && chan != null) SendJson("t", "bye", "reason", "The helper ended the session.");
            State = ViewerState.Ended; EndReason = reason; Status = reason;
            Files.CancelAll();
            SoundOn = false; { var pl = Player; if (pl != null) try { pl.Stop(); } catch { } }
            StopRecording("The session ended, so the recording stopped.", false);   // v1.5
            var dl = direct; direct = null; if (dl != null) { dl.Drain(1500); dl.Close("session ended"); }   // "bye" first, then the relay
            if (link != null) link.Close(reason);
            if (ecdh != null) ecdh.Dispose();
            Fire();
            var h = Ended; if (h != null) try { h(reason); } catch { }
        }
    }
}

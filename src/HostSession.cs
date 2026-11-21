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
        public bool Unattended; public string PersistSecret; public byte[] UnattendedKey;
        public bool AllowDirect = true;   // v1.3: connect out to the helper's PC directly when it's on the same network
        public IAudioSource Audio;        // v1.4: this PC's sound (only while Share sound is on and the helper asked for it)
        public bool SoundUnattended;      // v1.4: unattended sessions start with Share sound on (a setting, off by default)
        public ScreenSource Screen; public IInputSink Input; public IClip Clip; public string DownloadDir;
        public IFileClip FileClip;        // v1.5: copied files (only while clipboard sharing is on)
        public bool AskOwnFiles;          // v1.7: unattended sessions ask before copied files leave too (a setting, off by default)
    }

    public class HostSession
    {
        readonly HostOptions o;
        RelayLink link; Channel chan; EcdhKey ecdh; SessionKeys keys; byte[] pwKey; byte[] viewerPub;
        Transport tx; DirectLink direct; int directTried;   // v1.3
        bool peerFellBack;                                   // v1.4: the helper's app went back to the relay
        public bool Direct { get { var t = tx; return t != null && t.IsDirect; } }
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
        public event Action<string> Detail;                       // v1.4: connection details (the log), not shown on the bar or the main window
        public event Action<string, string, Action<bool>> FilesAskNeeded;   // v1.6: title, text, answer(allow) - the helper wants this PC's copied files
        public event Action FilesAskClosed;                                 // v1.6: the question went away (answered, timed out, session over)
        public event Action<string[]> FilesCopied;                          // v1.7: names - copied files left this PC in an unattended session (for the history and the bar)
        public bool AskOwnFiles { get { return o.AskOwnFiles; } set { o.AskOwnFiles = value; } }   // v1.7: the setting (also mid-session)
        void Tell(string s) { var h = Detail; if (h != null) try { h(s); } catch { } }

        public HostSession(HostOptions opts)
        {
            o = opts;
            Files = new Transfers(p => SendPlain(p), () => Queued, () => State == HostState.Connected, opts.DownloadDir ?? Transfers.DownloadFolder());
            Files.Note += s => Say(s);
            // v1.5: copied files that arrived are put on this PC's clipboard (only while clipboard sharing is on)
            Files.BatchReceived += (paths, clip) =>
            {
                if (!clip || !Clipboard || o.FileClip == null || State != HostState.Connected) return;
                o.FileClip.SetFiles(paths);
                Say((paths.Length == 1 ? "The file is" : "The " + paths.Length + " files are") + " on your clipboard too - paste with Ctrl+V into any folder.");
            };
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
            // v1.4: the old channel goes a little later, so what is already queued ("bye") is still sealed and sent - sealing
            // with a closed key used to fail in the relay sender and drop this PC off the relay (unattended: until the retry)
            if (chan != null) { var old = chan; chan = null; ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(5000); old.Dispose(); }); }
            if (ecdh != null) { ecdh.Dispose(); ecdh = null; }
            var d = direct; direct = null; tx = null; directTried = 0; peerFellBack = false; if (d != null) d.Close("session reset");
            StopSound(); SoundShared = false; soundWanted = false;
            RecordingAsked = false; RecordingNoticeShown = false; Files.PeerBatches = false; CancelPull();
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
                    var t = new Transport(link); t.Deliver = OnSealed; t.Overflow = r => DirectFailed(r); tx = t;
                    var hid = handshakeId;
                    new Thread(() => { Thread.Sleep(15000); lock (gate) if (handshakeId == hid && State == HostState.Checking) Kick("no hello"); }) { IsBackground = true }.Start();
                    return;
                }
                Kick("unexpected handshake message");
            }
        }

        void SendPlain(byte[] plain)
        {
            var c = chan; var t = tx; if (c == null || t == null) return;
            t.SendSealed(() => c.Seal(plain), plain.Length + 25);
        }
        long Queued { get { var t = tx; return t != null ? t.Queued : link == null ? 0 : link.Queued; } }

        // ---- v1.3: direct connection (the helper's app offers its home-network address; this PC connects out)
        void TryDirect(Dictionary<string, object> m)
        {
            if (!o.AllowDirect || State != HostState.Connected || Interlocked.Exchange(ref directTried, 1) == 1) return;
            object a; var addrs = new List<string>();
            if (m.TryGetValue("addrs", out a) && a is System.Collections.ArrayList) foreach (var x in (System.Collections.ArrayList)a) addrs.Add(Convert.ToString(x));
            byte[] token; try { token = Convert.FromBase64String(Msg.Str(m, "token") ?? ""); } catch { return; }
            var offered = DirectNet.ParseEndpoints(addrs);
            var c = chan; var t = tx; var hid = handshakeId;
            if (offered.Count == 0 || token.Length != DirectNet.TokenBytes || c == null || t == null) return;
            new Thread(() =>
            {
                // v1.4: only addresses on this PC's own subnets (and ports from 1024 up)
                var eps = DirectNet.OnMyNetworks(offered, DirectNet.Adapters());
                if (eps.Count == 0) { Tell("The helper's PC is on another network - staying on the relay."); return; }
                var d = DirectConnector.Connect(eps, token, 2500, 3000);
                if (d == null) { Tell("Couldn't reach the helper's PC directly - staying on the relay."); return; }
                lock (gate) if (handshakeId != hid || State != HostState.Connected) { d.Close("too late"); return; }
                d.Frame += t.OnDirectFrame;
                d.Closed += r => { if (direct == d) DirectFailed(r); };
                direct = d; d.Start();
                if (t.SwitchTo(d, () => c.Seal(Msg.JsonMsg("t", "switch")))) { Say("Connected directly over your network."); Fire(); }
                else { direct = null; d.Close("switch didn't happen"); }
            }) { IsBackground = true, Name = "direct connect" }.Start();
        }
        // v1.4: a direct link that drops mid-session goes back to the relay when the relay is still connected
        public void CutDirect(string why) { var d = direct; if (d != null) d.Close(why); }   // tests: the direct link drops
        void DirectFailed(string why)
        {
            var d = direct; direct = null; if (d != null) d.Close(why);
            if (State != HostState.Connected) return;
            var t = tx; var c = chan; var l = link;
            if (t != null && t.FellBack) return;   // already back on the relay
            if (t != null && c != null && l != null && l.IsOpen)
            {
                t.Fallback(() => c.Seal(Transport.MarkerPlain()));
                lock (inflight) inflight.Clear(); keyframe = true;   // acks lost with the link don't hold the picture up
                Tell("The direct connection dropped (" + why + ") - back on the relay.");
                Fire(); return;
            }
            Say("The direct connection dropped (" + why + ").");
            if (o.Unattended) { lock (gate) Kick("direct connection dropped"); }
            else End("The direct connection to your helper dropped. Click Get help again to reconnect.", false);
        }
        void SendJson(params object[] kv) { SendPlain(Msg.JsonMsg(kv)); }

        void OnSealed(byte[] b)
        {
            byte[] p;
            lock (gate)
            {
                if (chan == null) { Kick("sealed before the handshake"); return; }
                try { p = chan.Open(b, directTried == 1 && !peerFellBack ? (Func<byte[], bool>)Transport.IsMarker : null); }
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
                    Files.PeerBatches = Msg.Bool(m, "files2");   // v1.5
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
                case "direct-offer": TryDirect(m); break;
                case "switch": { var sx = tx; if (sx != null) sx.PeerSwitched(); break; }
                case "relay-again": { peerFellBack = true; var sx = tx; if (sx != null) sx.PeerFellBack(); if (direct != null) DirectFailed("the helper's app went back to the relay"); break; }
                case "sound-want":
                    soundWanted = Msg.Bool(m, "on");
                    if (soundWanted && !SoundShared && o.Audio != null) Say((HelperName ?? "Your helper") + " would like to hear this PC's sound - click Share sound on the bar if that's OK.");
                    UpdateSound(); break;
                case "ping": SendJson("t", "pong", "ts", Msg.Num(m, "ts")); break;
                case "chat": { var text = Msg.Str(m, "text") ?? ""; if (text.Length > 2000) text = text.Substring(0, 2000); var h = Chat; if (h != null) h(text); break; }
                case "clip": if (Clipboard && o.Clip != null) { var text = Msg.Str(m, "text") ?? ""; if (text.Length <= 1000000) { lastClip = text; o.Clip.Set(text); Say("The helper put text on your clipboard."); } } break;
                case "bye": End(Msg.Str(m, "reason") ?? "The helper ended the session.", false); break;
                // v1.6: the helper asks for the files copied on this PC - only while clipboard sharing is on, and this PC's person
                // is asked first (Allow / Deny, Deny after 60 s). "clip-files-get" is what a 1.5 helper sends: it's asked too.
                case "clip-files-ask": AskForFiles(false); break;
                case "clip-files-get": AskForFiles(true); break;
                // v1.5: session recording on the helper's side - only once this PC shows "Recording" on the red bar
                case "rec":
                    if (Msg.Bool(m, "on")) { if (!RecordingAsked) { RecordingAsked = true; RecordingNoticeShown = false; Say((HelperName ?? "Your helper") + " is recording this session - the red bar says Recording. Stop recording there whenever you like."); } }
                    else if (RecordingAsked) { RecordingAsked = false; RecordingNoticeShown = false; Say((HelperName ?? "Your helper") + " stopped recording."); }
                    Fire(); break;
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
                    SoundShared = o.Unattended && o.SoundUnattended && o.Audio != null;
                    WrongPasswords = 0;   // v1.1: only wrong tries in a row count towards the unattended pause
                    var mons = o.Screen.Monitors();
                    SendJson("t", "consent", "allowed", true, "control", Control, "clipboard", Clipboard, "unattended", o.Unattended, "sound", SoundShared, "soundAvailable", o.Audio != null, "files2", true, "rec", true, "askFiles", true,
                        "monitors", mons.Select((mm, i) => new Dictionary<string, object> { { "i", i }, { "w", mm.W }, { "h", mm.H }, { "primary", mm.Primary } }).ToArray());
                    monitor = Math.Max(0, Array.FindIndex(mons, mm => mm.Primary));
                    keyframe = true; inflight.Clear();
                    // v1.4: your own PC tells its owner's app its network adapter (for Wake-on-LAN) - unattended sessions only
                    if (o.Unattended) { var mac = DirectNet.PrimaryMac(); if (mac != null) SendJson("t", "mac", "mac", mac); }
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
            Clipboard = on; lastClip = null; SendJson("t", "clipboard", "on", on);
            bool old = pullOld;
            if (!on && CancelPull()) PullReply(false, "cancelled", "They turned off clipboard sharing, so nothing was sent.", old);
            Fire();
        }

        // ---- v1.6: the helper's "Copy their files here" - nothing leaves this PC before its person clicks Allow
        int pullId; volatile bool pullPending; bool pullOld;
        public bool PullPending { get { return pullPending; } }
        public int PullsAsked, PullsAllowed, PullsAuto;   // tests (PullsAuto, v1.7: allowed by itself in an unattended session)
        void PullReply(bool allowed, string reason, string text, bool old)
        {
            SendJson("t", "clip-files-answer", "allowed", allowed, "reason", reason, "text", text);
            if (old && !allowed) SendJson("t", "note", "text", FilePull.AnswerText(false, reason, text));   // a 1.5 helper only shows notes
        }
        void AskForFiles(bool old)
        {
            if (!Clipboard || o.FileClip == null) { PullReply(false, "clipboard", "They haven't turned on clipboard sharing, so their copied files can't be sent.", old); return; }
            if (pullPending) { SendJson("t", "note", "text", "They're still being asked about your last request."); return; }
            var files = o.FileClip.GetFiles();
            if (files == null || files.Length == 0) { PullReply(false, "nothing", "Nothing is copied on their PC - ask them to copy the files in Explorer first.", old); return; }
            PullAsk ask; var err = FilePull.Prepare(files, out ask);
            if (err != null) { PullReply(false, "error", "Their copied files can't be sent: " + err, old); return; }
            int id, cid;
            lock (gate) { id = ++pullId; cid = consentId; pullPending = true; pullOld = old; PullsAsked++; }
            var helper = HelperName ?? "Your helper";
            // v1.7: one of your own PCs (an unattended session - connected with the unattended password) allows it by itself,
            // unless "Ask before my own PCs share files in unattended sessions" is on. The copy goes into the connection history.
            if (o.Unattended && !o.AskOwnFiles) { lock (gate) PullsAuto++; PullAnswered(id, cid, ask, PullResult.Allow, old, false); return; }
            Say(helper + " wants to copy " + (ask.Names.Length == 1 ? "a file" : ask.Names.Length + " files") + " you copied - nothing is sent unless you click Allow.");
            var answer = FilePull.Once(r => PullAnswered(id, cid, ask, r, old, true), FilePull.TimeoutMs);
            var h = FilesAskNeeded;
            if (h == null) answer(PullResult.Deny);
            else try { h(FilePull.Title(helper, ask.Names.Length), FilePull.Text(ask), ok => answer(ok ? PullResult.Allow : PullResult.Deny)); } catch { answer(PullResult.Deny); }
            Fire();
        }
        void PullAnswered(int id, int cid, PullAsk ask, PullResult r, bool old, bool asked)
        {
            lock (gate) { if (id != pullId || !pullPending) return; pullPending = false; }
            if (asked) { var closed = FilesAskClosed; if (closed != null) try { closed(); } catch { } }
            if (State != HostState.Connected || consentId != cid) return;
            if (r != PullResult.Allow)
            {
                PullReply(false, r == PullResult.Timeout ? "timeout" : "deny", null, old);
                Say(r == PullResult.Timeout ? "No answer in time - your files were not sent." : "You said no - your files were not sent.");
                Fire(); return;
            }
            if (!Clipboard || o.FileClip == null) { PullReply(false, "cancelled", "They turned off clipboard sharing, so nothing was sent.", old); Fire(); return; }
            PullReply(true, asked ? "allow" : "auto", null, old);
            var err = Files.OfferMany(ask.Paths, true);   // exactly the files that were shown
            if (err != null) { PullReply(false, "error", "Their copied files can't be sent: " + err, old); Say("Your files couldn't be sent: " + err); Fire(); return; }
            PullsAllowed++;
            Say(asked ? "You allowed it - offering " + FilePull.List(ask) + " to " + (HelperName ?? "your helper") + "." : OwnFiles.BarText(HelperName) + ": " + FilePull.List(ask) + " (unattended session - see Connection history).");
            var fc = FilesCopied; if (fc != null && o.Unattended) try { fc(ask.Names); } catch { }   // v1.7: into this PC's connection history
            Fire();
        }
        // a question that's still open goes away (as Deny); true if there was one
        bool CancelPull()
        {
            bool was;
            lock (gate) { was = pullPending; pullPending = false; pullId++; }
            if (was) { var closed = FilesAskClosed; if (closed != null) try { closed(); } catch { } }
            return was;
        }
        // v1.4: Share sound (the red bar). Sound only flows while this is on AND the helper turned Sound on.
        public void SetSound(bool on)
        {
            if (State != HostState.Connected || o.Audio == null) return;
            SoundShared = on; SendJson("t", "sound", "on", on); UpdateSound(); Fire();
        }
        public bool SoundAvailable { get { return o.Audio != null; } }
        // v1.5: this PC's copied files to the helper (they're asked first) - only while clipboard sharing is on
        public string SendCopiedFiles()
        {
            if (State != HostState.Connected) return "Not connected.";
            if (!Clipboard || o.FileClip == null) return "Turn on clipboard sharing first.";
            return Files.OfferMany(o.FileClip.GetFiles(), true);
        }
        // v1.5: recording. The helper's app only records after RecordingNoticeShown: the UI calls NoticeShown() once the red bar
        // says "Recording" (NoticeUp), and while it keeps saying so this PC confirms every few seconds - when the confirmations
        // stop, the helper's app stops recording.
        public volatile bool RecordingAsked, RecordingNoticeShown, NoticeUp; public long NoticeShownAt;
        public static int RecBeatMs { get { return AssistSettings.TestDir != null ? 1000 : 3000; } }
        public void NoticeShown()
        {
            if (!RecordingAsked || RecordingNoticeShown || !NoticeUp || State != HostState.Connected) return;
            RecordingNoticeShown = true; NoticeShownAt = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            SendJson("t", "rec-ok", "on", true);
        }
        public void StopRecording()
        {
            if (!RecordingAsked || State != HostState.Connected) return;
            RecordingAsked = false; RecordingNoticeShown = false;
            SendJson("t", "rec", "on", false); Say("You stopped the recording."); Fire();
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
            CancelPull();   // v1.6: an open question about copied files goes away
            Crash.Secrets = new string[0];
            Password = null;
            StopSound();
            var dl = direct; direct = null; if (dl != null) { dl.Drain(1500); dl.Close("session ended"); }   // "bye" first, then the relay
            if (link != null) link.Close(reason);
            Fire();
            var h = Ended; if (h != null) try { h(reason); } catch { }
        }

        // ---- v1.4: sound
        public volatile bool SoundShared; volatile bool soundWanted; volatile bool soundRunning; public long AudioPackets;
        readonly object soundGate = new object();
        public bool SoundFlowing { get { return soundRunning; } }
        void UpdateSound()
        {
            bool want = State == HostState.Connected && SoundShared && soundWanted && o.Audio != null;
            lock (soundGate)
            {
                if (want == soundRunning) return;
                soundRunning = want;
                if (!want) { try { o.Audio.Stop(); } catch { } return; }
                var st = new int[2]; var cid = consentId;
                o.Audio.Start(pcm =>
                {
                    if (!soundRunning || consentId != cid || State != HostState.Connected) return;
                    if (Queued > 512 * 1024) return;   // the picture and input come first; a dropped 40 ms is just a short gap
                    SendPlain(AudioMsg.FromPcm(pcm, pcm.Length, st).Encode()); AudioPackets++;
                });
            }
        }
        void StopSound() { lock (soundGate) { if (!soundRunning) return; soundRunning = false; try { if (o.Audio != null) o.Audio.Stop(); } catch { } } }

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
            var enc = new TileEncoder(); uint seq = 0; var sw = Stopwatch.StartNew(); long lastFrame = -1000, lastCheck = -1000, lastClipCheck = 0, lastBeat = 0;
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
                    // v1.5: "the red bar still says Recording" - without this the helper's app stops recording
                    if (RecordingNoticeShown && now - lastBeat >= RecBeatMs) { lastBeat = now; if (RecordingAsked && NoticeUp) SendJson("t", "rec-ok", "on", true); }
                    int interval = quality == "fast" ? 50 : 66;
                    int waiting; lock (inflight) waiting = inflight.Count;
                    if (now - lastFrame < interval || waiting >= 3 || Queued > 1024 * 1024) { Thread.Sleep(8); continue; }
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

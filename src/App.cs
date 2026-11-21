// Hallow Assist - remote help for friends and family: see and control a PC with their OK, from your PC, phone or TV browser.
// One exe, two roles: "Get help" (the person being helped) and "Help someone" (the helper). Sessions go through your own relay
// (server/ in the repo) and are end-to-end encrypted. There is no stealth mode, no hidden install and no command that starts
// a session: a session only starts when the person clicks Get help (or, on your own PCs only, with unattended access turned on).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

[assembly: AssemblyTitle("Hallow Assist")]
[assembly: AssemblyProduct("Hallow Assist")]
[assembly: AssemblyDescription("Remote help for friends and family")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace HallowAssist
{
    public static class Program
    {
        public const string Version = "1.4";
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr v);

        [STAThread]
        public static void Main(string[] args)
        {
            int ci = Array.IndexOf(args, "--cmd");
            if (ci >= 0) { Environment.Exit(Family.RunCmd(ci + 1 < args.Length ? args[ci + 1] : "")); return; }
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // per-monitor v2: real pixels for capture and input
            int si = Array.IndexOf(args, "--snap");
            if (si >= 0 && si + 1 < args.Length)
            {
                Snap.On = true; AssistSettings.Frozen = true;
                var sa = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(sa);
                SnapAll(args[si + 1]); return;
            }
            var leaf = AssistSettings.TestDir != null ? "Test" + Path.GetFileName(AssistSettings.TestDir.TrimEnd('\\')) : "";
            bool created;
            var mutex = new Mutex(true, @"Local\HallowAssist" + leaf, out created);
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\HallowAssistShow" + leaf);
            if (!created) { showEvent.Set(); return; }
            Crash.Install();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LoadTheme(app);
            app.DispatcherUnhandledException += (s, e) => { Crash.Write(e.Exception); e.Handled = true; Environment.Exit(1); };
            var c = new Controller(args.Contains("--tray"));
            new Thread(() => { while (showEvent.WaitOne()) app.Dispatcher.BeginInvoke(new Action(c.ShowMain)); }) { IsBackground = true }.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
        public static string AccentHex = Accent.Default;
        public static void LoadTheme(Application app)
        {
            string xaml;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("HallowAssist.Theme.xaml")) using (var r = new StreamReader(s)) xaml = r.ReadToEnd();
            AccentHex = Accent.Current(AssistSettings.Load().UseHallowAccent);
            app.Resources.MergedDictionaries.Clear();
            app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Accent.ApplyTo(xaml, AccentHex)));
        }

        // --snap <folder>: every window rendered off-screen with made-up data (never a real code, password or screen)
        static void SnapAll(string dir)
        {
            var c = new Controller(true, true);
            var main = new MainWindow(c);
            Snap.Save(main, Path.Combine(dir, "main.png"), 860, 0);
            c.SnapHost = new HostSession(new HostOptions { Screen = new SyntheticSource() }) { Code = "481207365", Password = "K7MX2P", State = HostState.Waiting };
            main.RefreshAll(); Snap.Save(main, Path.Combine(dir, "main-gethelp.png"), 860, 0);
            c.SnapHost.State = HostState.Connected; c.SnapHost.HelperName = "Dom"; c.SnapHost.Safety = "4827"; c.SnapHost.Control = true;
            main.RefreshAll(); Snap.Save(main, Path.Combine(dir, "main-connected.png"), 860, 0);
            c.SnapHost = null; c.S.Unattended = true; c.S.UnattendedCode = "715390246"; c.S.RelayUrl = "https://assist.example.com";
            main.Build(); Snap.Save(main, Path.Combine(dir, "main-unattended.png"), 860, 0);
            Snap.Save(new ConsentWindow("Dom", "4827", false), Path.Combine(dir, "consent.png"), 470, 0);
            var bar = new HelpBar(); bar.Update("Dom", true, false, false, "4827", true); Snap.Save(bar, Path.Combine(dir, "bar.png"), 860, 0);
            var bar2 = new HelpBar(); bar2.Update("Dom", false, true, false, "4827", true); Snap.Save(bar2, Path.Combine(dir, "bar-viewonly.png"), 860, 0);
            var bar3 = new HelpBar(); bar3.Update("Dom", true, false, true, "4827", true); Snap.Save(bar3, Path.Combine(dir, "bar-unattended.png"), 900, 0);
            var bar4 = new HelpBar(); bar4.Update("Dom", true, false, false, "4827", true, true, true); Snap.Save(bar4, Path.Combine(dir, "bar-sound.png"), 1080, 0);
            var vs = new ViewerSession(new Uri("wss://assist.example.com/ws"), "481207365", "K7MX2P", "Dom") { HostUnattended = true, HostSharesSound = true, HostHasSound = true, SoundOn = true };
            var vw = new ViewerWindow(vs, "481207365"); vw.SnapFill(); Snap.Save(vw, Path.Combine(dir, "viewer.png"), 1180, 760);
            var vs2 = new ViewerSession(new Uri("wss://assist.example.com/ws"), "481207365", "K7MX2P", "Dom") { Safety = "4827", State = ViewerState.Waiting, Status = "Connected securely. Waiting for them to click Allow..." };
            var vw2 = new ViewerWindow(vs2, "481207365"); vw2.Refresh(); Snap.Save(vw2, Path.Combine(dir, "viewer-waiting.png"), 1180, 560);
            var vs3 = new ViewerSession(new Uri("wss://assist.example.com/ws"), "481207365", "K7MX2P", "Dom");
            var vw3 = new ViewerWindow(vs3, "481207365"); vw3.SnapFill(); vs3.Banner = "They need to click the admin prompt themselves - Windows hides it from remote help."; vw3.Refresh(); Snap.Save(vw3, Path.Combine(dir, "viewer-uac.png"), 1180, 760);
            Snap.Save(new PromptWindow("Dom wants to send you a file", "setup-notes.txt (12 KB). It goes into Downloads\\Hallow Assist.", "Accept", "Decline", ok => { }), Path.Combine(dir, "file-prompt.png"), 420, 0);
            var sd = new SavePcDialog(null, true); sd.SnapFill("Living room PC", "715 390 246"); Snap.Save(sd, Path.Combine(dir, "save-pc.png"), 480, 0);
            c.S.RelayUrl = "https://assist.example.com"; c.S.Unattended = false;
            c.S.Pcs = AssistSettings.CleanPcs(new System.Collections.Generic.List<SavedPc> { new SavedPc { Name = "Living room PC", Code = "715390246", Key = "x", LastUsed = DateTime.Now.ToString("o") }, new SavedPc { Name = "Mum's laptop", Code = "302118457" }, new SavedPc { Name = "Office PC", Code = "560913284", Key = "x", Mac = "3C7C3F1A2B4D" } });
            c.PcStatus = new Dictionary<string, string> { { "715390246", "online" }, { "302118457", "offline" }, { "560913284", "offline" } };
            main.Build(); Snap.Save(main, Path.Combine(dir, "main-yourpcs.png"), 860, 0);
            var now = DateTime.Now;
            var hist = new List<HistoryEntry> {
                new HistoryEntry { Start = now.AddMinutes(-42).ToString("o"), End = now.AddMinutes(-18).ToString("o"), Helper = "Dom", Client = "app", ControlAllowed = true, InputUsed = true, FilesReceived = 1 },
                new HistoryEntry { Start = now.AddDays(-1).AddHours(-3).ToString("o"), End = now.AddDays(-1).AddHours(-3).AddMinutes(6).ToString("o"), Helper = "Dom", Client = "browser", Unattended = true, ControlAllowed = true, InputUsed = true },
                new HistoryEntry { Start = now.AddDays(-9).ToString("o"), End = now.AddDays(-9).AddMinutes(3).ToString("o"), Helper = "Sam", Client = "browser", ControlAllowed = false } };
            Snap.Save(new HistoryWindow(hist, () => { }), Path.Combine(dir, "history.png"), 620, 0);
            var ud = new UnattendedDialog(); ud.SnapFill("Use at least 12 characters."); Snap.Save(ud, Path.Combine(dir, "unattended.png"), 480, 0);
            var chat = new ChatWindow("Dom"); chat.Add("Them", "Can you see the printer settings?", false); chat.Add("You", "Yes - click Devices on the left.", true); Snap.Save(chat, Path.Combine(dir, "chat.png"), 380, 420);
            Snap.Save(CrashNotice.Make(Path.Combine(dir, "example.txt")), Path.Combine(dir, "crash.png"), 440, 0);
        }
    }

    // the app: tray icon, family pipe, the main window, sessions in both roles, the bar and the panic hotkey
    public class Controller
    {
        public readonly AssistSettings S = AssistSettings.Load();
        MainWindow main; WF.NotifyIcon tray; HelpBar bar; ConsentWindow consent; ChatWindow hostChat; ViewerWindow viewerWin;
        HostSession host; public HostSession SnapHost;
        public HostSession Host { get { return SnapHost ?? host; } }
        public ViewerSession Viewer;
        readonly bool snap; bool hubHosts, hotkeyOk, quitting;
        readonly Dispatcher ui;
        HwndSource hotkeyWin; const int HotkeyId = 0xA551;
        // headless test mode: answers that a person would give, through the test pipe
        Action<ConsentAnswer> pendingConsent; readonly Queue<Action<bool>> pendingFiles = new Queue<Action<bool>>();
        readonly List<string> log = new List<string>();
        // v1.2
        DrawOverlay overlay; public int DrawStrokes;
        HistoryEntry histEntry; HostSession histHost; long histInputStart; int histFiles;
        public Dictionary<string, string> PcStatus = new Dictionary<string, string>(); bool statusBusy;
        class RestartWait { public string Code, Name; public byte[] Key; public DateTime Since; public bool SawOffline; }
        RestartWait restartWait;

        public Controller(bool tray, bool snapMode = false)
        {
            snap = snapMode; ui = Dispatcher.CurrentDispatcher;
            if (snap) return;
            Family.Serve("Assist", Command);
            if (!AssistSettings.Headless)
            {
                SetupTray();
                var host2 = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }; host2.Tick += (s, e) => CheckHost(); host2.Start(); CheckHost();
                accentWatch = Accent.Watch(() => ui.BeginInvoke(new Action(ApplyAccent)));
                if (AssistSettings.TestDir == null || Environment.GetEnvironmentVariable("HALLOW_ASSIST_API") != null)
                {
                    var upd = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                    upd.Tick += (s, e) => { upd.Interval = TimeSpan.FromHours(6); DateTime last; if (!DateTime.TryParse(S.LastUpdateCheck, out last) || (DateTime.Now - last).TotalHours >= 24) CheckForUpdate(false); };
                    upd.Start();
                }
            }
            // a hidden message window for the panic hotkey (Ctrl+Alt+End)
            hotkeyWin = new HwndSource(new HwndSourceParameters("HallowAssistHotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
            hotkeyWin.AddHook(HotkeyHook);
            WriteStatus();
            var st = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) }; st.Tick += (s, e) => { WriteStatus(); RefreshPcStatus(); }; st.Start();
            var rt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) }; rt.Tick += (s, e) => CheckRestartWait(); rt.Start();
            if (S.Unattended) RetryUnattended();
            if (!tray && !AssistSettings.Headless) ShowMain();
            if (!AssistSettings.Headless) ui.BeginInvoke(new Action(ShowCrashNotice), DispatcherPriority.ApplicationIdle);
        }
        void Log(string s) { lock (log) { log.Add(DateTime.Now.ToString("HH:mm:ss") + " " + s); if (log.Count > 50) log.RemoveAt(0); } }

        public void ShowMain()
        {
            if (AssistSettings.Headless) return;
            if (main == null) main = new MainWindow(this);
            main.Show(); if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal; main.Activate();
            RefreshPcStatus();
        }
        // closing the main window: stays in the tray only for unattended access or a running session; otherwise quits
        public void MainClosing()
        {
            bool busy = (host != null && host.State != HostState.Ended && !host.Unattended) || (Viewer != null && Viewer.State != ViewerState.Ended);
            if (busy)
            {
                var r = MessageBox.Show(main, "Closing Hallow Assist ends the session. Close it?", "Hallow Assist", MessageBoxButton.YesNo);
                if (r != MessageBoxResult.Yes) return;
                Quit(); return;
            }
            if (S.Unattended) { main.Hide(); Notify("Hallow Assist", "Still running for unattended access. Right-click the icon to quit."); return; }
            Quit();
        }
        public void Quit()
        {
            if (quitting) return; quitting = true;
            FinishHistory();
            if (host != null) host.End("Hallow Assist was closed.", true);
            if (Viewer != null) Viewer.End("Hallow Assist was closed.", true);
            UnregisterHotkey();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (bar != null) bar.CloseForGood();
            WriteStatus(false);
            if (main != null) main.Exiting = true;
            // give the "bye" messages a moment to leave
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) }; t.Tick += (s, e) => { t.Stop(); Application.Current.Shutdown(); }; t.Start();
        }

        // ---------------- Get help (the person being helped)
        public void GetHelp()
        {
            var uri = AssistSettings.WsUri(S.RelayUrl);
            if (uri == null) { if (main != null) main.Say("Set the relay address in Settings first.", Ui.Amber); return; }
            if (host != null && host.State != HostState.Ended && !host.Unattended) return;
            if (host != null && host.Unattended) { unattendedStopped = true; host.End("Paused while you get help.", false); }
            StartHost(new HostOptions { Relay = uri, Quality = S.Quality });
        }
        void StartHost(HostOptions o)
        {
            o.AllowDirect = !S.DirectOff;
            o.Screen = AssistSettings.Fake ? new SyntheticSource() : ScreenSource.Create();
            o.Input = AssistSettings.Fake ? (IInputSink)new RecordingSink(AssistSettings.Dir) : new SendInputSink();
            o.Clip = AssistSettings.TestDir != null ? (IClip)new FileClip(Path.Combine(AssistSettings.Dir, "clipboard.txt")) : new WpfClip(ui);
            o.Audio = AssistSettings.Fake ? (IAudioSource)new ToneSource() : new LoopbackSource();   // v1.4: only used while Share sound is on
            o.SoundUnattended = S.SoundUnattended;
            var h = new HostSession(o);
            host = h;
            h.Detail += t => ui.BeginInvoke(new Action(() => Log(t)));
            h.Changed += () => ui.BeginInvoke(new Action(() => HostChanged(h)));
            h.ConsentNeeded += (name, safety, answer) => ui.BeginInvoke(new Action(() => AskConsent(h, name, safety, answer)));
            h.Chat += t => ui.BeginInvoke(new Action(() => { Log("chat from helper: " + t); if (AssistSettings.Headless) return; OpenHostChat(h); hostChat.Add(h.HelperName, t, false); }));
            h.Note += t => ui.BeginInvoke(new Action(() => { Log(t); if (main != null) main.Say(t); }));
            h.Files.Offered += (name, size, answer) => ui.BeginInvoke(new Action(() =>
            {
                Log("file offered: " + name);
                if (AssistSettings.Headless) { lock (pendingFiles) pendingFiles.Enqueue(answer); return; }
                new PromptWindow((h.HelperName ?? "Your helper") + " wants to send you a file", name + " (" + Transfers.Size(size) + "). It goes into " + h.Files.Folder + ". Only accept files you expect." + Transfers.Warning(name), "Accept", "Decline", answer).Show();
            }));
            h.Ended += r => ui.BeginInvoke(new Action(() => HostEnded(h, r)));
            // v1.2: drawing, restart, files for the history
            h.Drawing += (m, id, pts) => ui.BeginInvoke(new Action(() =>
            {
                if (h != host) return; DrawStrokes++;
                if (AssistSettings.Headless) return;
                if (overlay == null) overlay = new DrawOverlay();
                overlay.Add(m, id, pts);
            }));
            h.DrawClear += () => ui.BeginInvoke(new Action(() => { if (overlay != null) overlay.Clear(); }));
            h.RestartRequested += who => ui.BeginInvoke(new Action(() => RestartPc(h, who)));
            h.Files.Received += p => ui.BeginInvoke(new Action(() => { if (h == histHost) histFiles++; }));
            h.WrongPassword += () => ui.BeginInvoke(new Action(() => { if (h.Unattended && h.WrongPasswords >= 5) { S.UnattendedLockedUntil = DateTime.Now.AddMinutes(15).ToString("o"); S.Save(); } }));
            h.Start();
            HostChanged(h);
        }
        void HostChanged(HostSession h)
        {
            if (h != host && h != SnapHost) return;
            if (h != SnapHost) TrackHistory(h);
            if (h.State != HostState.Connected && overlay != null) { overlay.Close(); overlay = null; }
            bool live = h.State == HostState.Asking || h.State == HostState.Connected;
            if (live) RegisterHotkey(); else if (Viewer == null || true) UnregisterHotkey();
            if (h.State != HostState.Asking && consent != null) { consent.Cancel(); consent = null; }
            if (h.State == HostState.Connected)
            {
                if (!AssistSettings.Headless)
                {
                    if (bar == null)
                    {
                        bar = new HelpBar();
                        bar.Disconnect = () => StopHost("You disconnected.");
                        bar.ToggleControl = () => h.SetControl(!h.Control);
                        bar.ToggleClipboard = () => h.SetClipboard(!h.Clipboard);
                        bar.ToggleSound = () => h.SetSound(!h.SoundShared);
                        bar.OpenChat = () => OpenHostChat(h);
                        bar.SendFile = () => { var d = new OpenFileDialog { Title = "Send a file to your helper (they're asked first)" }; if (d.ShowDialog() == true) { var e = h.SendFile(d.FileName); if (e != null && main != null) main.Say(e, Ui.Amber); } };
                    }
                    bar.Update(h.HelperName, h.Control, h.Clipboard, h.Unattended, h.Safety, hotkeyOk, h.SoundShared, h.SoundAvailable);
                    if (!bar.IsVisible) bar.Show();
                }
                barVisible = true;
            }
            else HideBar();
            if (main != null) main.RefreshAll();
            UpdateTrayText();
            WriteStatus();
        }
        bool barVisible;
        void HideBar() { barVisible = false; if (bar != null) { bar.CloseForGood(); bar = null; } if (hostChat != null) { hostChat.ForceClose(); hostChat = null; } }
        void AskConsent(HostSession h, string name, string safety, Action<ConsentAnswer> answer)
        {
            if (h != host) { answer(new ConsentAnswer()); return; }
            Log("consent asked for " + name);
            if (AssistSettings.Headless) { pendingConsent = answer; WriteStatus(); return; }
            consent = new ConsentWindow(name, safety, h.HelperClient == "browser");
            consent.Answered += a => answer(a);
            consent.Show(); consent.Activate();
        }
        void OpenHostChat(HostSession h)
        {
            if (hostChat == null) { hostChat = new ChatWindow(h.HelperName ?? "your helper"); hostChat.Send = t => h.SendChat(t); }
            if (!hostChat.IsVisible) hostChat.Show();
        }
        public void StopHost(string why) { var h = host; if (h != null) h.Disconnect(why); }
        bool unattendedStopped; int unattendedBackoff = 10;
        void HostEnded(HostSession h, string reason)
        {
            Log("host ended: " + reason);
            pendingConsent = null;
            HostChanged(h);
            if (h != host) return;
            if (!h.Unattended && !string.IsNullOrEmpty(reason) && main == null && !AssistSettings.Headless) Notify("Hallow Assist", reason);
            // unattended: stay available - reconnect after a pause (longer each time the relay can't be reached)
            if ((h.Unattended && !unattendedStopped) || (!h.Unattended && S.Unattended && !quitting))
            {
                var wait = h.Unattended ? unattendedBackoff : 3; unattendedBackoff = Math.Min(300, unattendedBackoff * 2);
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(wait) };
                t.Tick += (s, e) => { t.Stop(); RetryUnattended(); };
                t.Start();
            }
        }
        // v1.1: after 5 wrong unattended passwords access pauses for 15 minutes - and now really comes back afterwards
        // (before, the retry found it locked and gave up until the app was restarted, so your own PC stayed unreachable)
        void RetryUnattended()
        {
            if (quitting || !S.Unattended || (host != null && host.State != HostState.Ended)) return;
            var left = UnattendedLockLeft();
            if (left > TimeSpan.Zero)
            {
                var t = new DispatcherTimer { Interval = left + TimeSpan.FromSeconds(1) };
                t.Tick += (s, e) => { t.Stop(); RetryUnattended(); };
                t.Start(); return;
            }
            if (UnattendedAllowed()) StartUnattended();
        }
        TimeSpan UnattendedLockLeft()
        {
            DateTime until;
            if (!DateTime.TryParse(S.UnattendedLockedUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out until)) return TimeSpan.Zero;
            var left = until - DateTime.Now; return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        // ---------------- unattended access (own PCs only; off by default)
        bool UnattendedAllowed()
        {
            DateTime until; if (DateTime.TryParse(S.UnattendedLockedUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out until) && until > DateTime.Now) return false;
            return S.Unattended && StartsWithWindows() && AssistSettings.WsUri(S.RelayUrl) != null;
        }
        void StartUnattended()
        {
            var secret = AssistSettings.Unprotect(S.UnattendedSecret); var key = AssistSettings.Unprotect(S.UnattendedKey);
            if (secret == null || key == null) { Log("unattended secrets unreadable - turned off"); S.Unattended = false; S.Save(); return; }
            unattendedStopped = false;
            StartHost(new HostOptions { Relay = AssistSettings.WsUri(S.RelayUrl), Unattended = true, PersistSecret = System.Text.Encoding.ASCII.GetString(secret), UnattendedKey = key, Quality = S.Quality });
            host.Changed += () => { if (host != null && host.State == HostState.Connected) unattendedBackoff = 10; };
        }
        public void SetUnattended(bool on)
        {
            if (on)
            {
                if (AssistSettings.WsUri(S.RelayUrl) == null) { MessageBox.Show(main, "Set the relay address first.", "Hallow Assist"); main.Build(); return; }
                var d = new UnattendedDialog { Owner = main };
                if (d.ShowDialog() != true) { main.Build(); return; }
                EnableUnattended(d.Password);
            }
            else
            {
                S.Unattended = false; S.UnattendedSecret = null; S.UnattendedKey = null; S.UnattendedCode = null; S.Save();
                unattendedStopped = true;
                if (host != null && host.Unattended) host.End("Unattended access turned off.", true);
                SetStartWithWindows(false);
            }
            if (main != null) main.Build();
            WriteStatus();
        }
        void EnableUnattended(string password)
        {
            var secret = Kdf.RandomHex(32); var code = Kdf.PersistentCode(secret);
            S.UnattendedSecret = AssistSettings.Protect(System.Text.Encoding.ASCII.GetBytes(secret));
            S.UnattendedKey = AssistSettings.Protect(Kdf.PasswordKey(password, code));
            S.UnattendedCode = code; S.Unattended = true; S.UnattendedLockedUntil = null; S.Save();
            SetStartWithWindows(true);
            if (host == null || host.State == HostState.Ended) StartUnattended();
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string RunFile { get { return Path.Combine(AssistSettings.Dir, "run-key-test.txt"); } }   // tests never touch the real Run key
        bool StartsWithWindows()
        {
            if (AssistSettings.TestDir != null) return File.Exists(RunFile);
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("HallowAssist") != null; } catch { return false; }
        }
        void SetStartWithWindows(bool on)
        {
            if (AssistSettings.TestDir != null) { if (on) File.WriteAllText(RunFile, "\"HallowAssist.exe\" --tray"); else if (File.Exists(RunFile)) File.Delete(RunFile); return; }
            try { using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) { if (on) k.SetValue("HallowAssist", "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray"); else k.DeleteValue("HallowAssist", false); } } catch { }
        }

        // ---------------- Help someone (the helper)
        public void Connect(string code, string password, string name)
        {
            var uri = AssistSettings.WsUri(S.RelayUrl); if (uri == null) return;
            if (Viewer != null && Viewer.State != ViewerState.Ended) return;
            StartViewer(new ViewerSession(uri, code, password, name), code);
        }
        // v1.1: Your PCs - connect with the remembered key; false = no usable key (type the password instead)
        public bool ConnectSaved(SavedPc pc)
        {
            var uri = AssistSettings.WsUri(S.RelayUrl); if (uri == null || pc == null) return false;
            if (Viewer != null && Viewer.State != ViewerState.Ended) return false;
            var key = AssistSettings.UnprotectPcKey(pc.Key);
            if (key == null) { if (pc.Key != null) { pc.Key = null; S.Save(); } return false; }
            var name = S.Name.Trim().Length > 0 ? S.Name.Trim() : Environment.UserName;
            StartViewer(new ViewerSession(uri, pc.Code, key, name), pc.Code);
            return true;
        }
        public void SavePc(string name, string code, byte[] key, string mac = null)
        {
            code = Kdf.NormCode(code); if (code.Length != 9) return;
            var pc = S.FindPc(code);
            if (pc == null) { if (S.Pcs.Count >= AssistSettings.MaxPcs) return; pc = new SavedPc { Code = code }; S.Pcs.Add(pc); }
            pc.Name = name; pc.Key = key == null ? null : AssistSettings.ProtectPcKey(key);
            if (mac != null) pc.Mac = mac;
            S.Pcs = AssistSettings.CleanPcs(S.Pcs); S.Save();
            if (main != null) main.RefreshHelpSomeone();
            RefreshPcStatus();
        }
        public void ForgetPc(string code) { var pc = S.FindPc(code); if (pc == null) return; S.Pcs.Remove(pc); S.Save(); if (main != null) main.RefreshHelpSomeone(); }
        // ---------------- v1.2: connection history (on the PC being helped)
        void TrackHistory(HostSession h)
        {
            if (h.State == HostState.Connected)
            {
                if (histEntry == null || histHost != h || (histEntry.Helper != h.HelperName))
                {
                    FinishHistory();
                    histEntry = new HistoryEntry { Start = DateTime.Now.ToString("o"), Helper = h.HelperName, Client = h.HelperClient, Unattended = h.Unattended, ControlAllowed = h.Control };
                    histHost = h; histInputStart = h.InputEvents; histFiles = 0;
                }
                else if (h.Control) histEntry.ControlAllowed = true;
            }
            else if (histEntry != null && histHost == h) FinishHistory();
        }
        void FinishHistory()
        {
            var e = histEntry; var h = histHost; histEntry = null; histHost = null;
            if (e == null || h == null) return;
            e.End = DateTime.Now.ToString("o"); e.InputUsed = h.InputEvents > histInputStart; e.FilesReceived = histFiles;
            History.Add(e);
        }
        public void ShowHistory()
        {
            var w = new HistoryWindow(History.Load(), () => History.Clear()) { Owner = main };
            w.ShowDialog();
        }

        // ---------------- v1.2: restart (unattended PCs only - the helper's side asked for it)
        void RestartPc(HostSession h, string who)
        {
            if (h != host || !h.Unattended || h.State != HostState.Connected) return;
            var args = Power.RestartArgs(who);
            Log("restart requested by " + (who ?? "the helper"));
            if (AssistSettings.TestDir != null) { try { File.WriteAllText(Path.Combine(AssistSettings.Dir, "restart-requested.txt"), args); } catch { } return; }   // tests never restart the PC
            FinishHistory();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true }); }
            catch (Exception e) { Log("restart failed: " + e.Message); }
        }

        // ---------------- v1.2: after a restart, wait for that PC to come back online, then connect again by itself
        void BeginRestartWait(ViewerSession v)
        {
            restartWait = new RestartWait { Code = v.Code, Key = v.PasswordKey, Name = v.HelperName, Since = DateTime.Now };
            var pc = S.FindPc(v.Code);
            if (main != null) main.Say("Restarting " + (pc != null ? pc.Name : Ui.FormatCode(v.Code)) + " - Hallow Assist connects again when it's back (up to 6 minutes).");
        }
        void CheckRestartWait()
        {
            var w = restartWait; if (w == null || statusBusy) return;
            var uri = AssistSettings.WsUri(S.RelayUrl);
            var pc = S.FindPc(w.Code); var label = pc != null ? pc.Name : Ui.FormatCode(w.Code);
            if (uri == null || quitting) { restartWait = null; return; }
            if ((DateTime.Now - w.Since).TotalMinutes > 6)
            {
                restartWait = null;
                if (main != null) { main.RefreshHelpSomeone(); main.Say(label + " didn't come back within 6 minutes. If Windows is waiting for someone to sign in, Hallow Assist starts after that.", Ui.Amber); }
                return;
            }
            if ((DateTime.Now - w.Since).TotalSeconds < 20) return;   // it's still shutting down
            statusBusy = true;
            new Thread(() =>
            {
                var r = RelayStatus.Query(uri, new[] { w.Code });
                ui.BeginInvoke(new Action(() =>
                {
                    statusBusy = false;
                    if (restartWait != w || r == null) return;
                    string st; r.TryGetValue(w.Code, out st);
                    if (st == "offline") w.SawOffline = true;
                    // only after it was seen gone (or 2 minutes passed): before that, "online" is still the old session
                    if (st == "online" && (w.SawOffline || (DateTime.Now - w.Since).TotalMinutes >= 2) && (Viewer == null || Viewer.State == ViewerState.Ended))
                    {
                        restartWait = null;
                        if (main != null) main.Say(label + " is back - connecting.");
                        var u2 = AssistSettings.WsUri(S.RelayUrl); if (u2 != null) StartViewer(new ViewerSession(u2, w.Code, w.Key, w.Name), w.Code);
                    }
                }));
            }) { IsBackground = true, Name = "restart wait" }.Start();
        }
        public bool WaitingForRestart { get { return restartWait != null; } }
        public void CancelRestartWait() { restartWait = null; if (main != null) main.Say("Stopped waiting."); }

        // ---------------- v1.4: Wake-on-LAN for Your PCs - magic packets on this network, then wait for its online dot
        class WakeWait { public string Code, Name; public DateTime Since; }
        WakeWait wakeWait; DispatcherTimer wakeTimer; public string WakeState = "";
        public bool Waking(string code) { var w = wakeWait; return w != null && w.Code == Kdf.NormCode(code); }
        public void Wake(SavedPc pc)
        {
            if (pc == null || Wol.ParseMac(pc.Mac) == null) return;
            var targets = Wol.Targets(); var mac = pc.Mac; var label = pc.Name;
            new Thread(() =>
            {
                int sent = 0; try { sent = Wol.Send(mac, targets); } catch { }
                ui.BeginInvoke(new Action(() =>
                {
                    Log("wake: " + sent + " packets for " + label);
                    if (sent == 0) { WakeState = "failed"; if (main != null) main.Say("Couldn't send the wake-up from this PC (no network?).", Ui.Amber); return; }
                    WakeState = "sent";
                    wakeWait = new WakeWait { Code = pc.Code, Name = label, Since = DateTime.Now };
                    if (main != null) { main.Say("Sent a wake-up to " + label + ". Waiting for it to come online (up to 2 minutes)... This only works on the same network, with Wake-on-LAN turned on in its BIOS and network adapter."); main.RefreshHelpSomeone(); }
                    if (wakeTimer == null) { wakeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AssistSettings.TestDir != null ? 1 : 5) }; wakeTimer.Tick += (s, e) => CheckWake(); }
                    wakeTimer.Start(); CheckWake();
                }));
            }) { IsBackground = true, Name = "wake" }.Start();
        }
        bool wakeBusy;
        void CheckWake()
        {
            var w = wakeWait; if (w == null) { if (wakeTimer != null) wakeTimer.Stop(); return; }
            if ((DateTime.Now - w.Since).TotalMinutes > 2)
            {
                wakeWait = null; WakeState = "timeout"; if (wakeTimer != null) wakeTimer.Stop();
                if (main != null) { main.Say(w.Name + " didn't come online within 2 minutes. Wake-on-LAN only works on the same network, with it turned on in the BIOS and the network adapter's settings - and Hallow Assist has to start with Windows there.", Ui.Amber); main.RefreshHelpSomeone(); }
                return;
            }
            var uri = AssistSettings.WsUri(S.RelayUrl); if (uri == null || wakeBusy) return;
            wakeBusy = true;
            new Thread(() =>
            {
                var r = RelayStatus.Query(uri, new[] { w.Code });
                ui.BeginInvoke(new Action(() =>
                {
                    wakeBusy = false;
                    string st = null; if (r != null) r.TryGetValue(w.Code, out st);
                    if (wakeWait != w || st == null) return;
                    PcStatus[w.Code] = st; if (main != null) main.ShowPcStatus();
                    if (st == "online" || st == "busy")
                    {
                        wakeWait = null; WakeState = "online"; if (wakeTimer != null) wakeTimer.Stop();
                        if (main != null) { main.Say(w.Name + " is awake and online.", Ui.Green); main.RefreshHelpSomeone(); }
                    }
                }));
            }) { IsBackground = true, Name = "wake wait" }.Start();
        }
        public void CancelWake() { wakeWait = null; WakeState = ""; if (wakeTimer != null) wakeTimer.Stop(); if (main != null) main.RefreshHelpSomeone(); }

        // ---------------- v1.2: online dots for Your PCs (only while the main window is open)
        public void RefreshPcStatus()
        {
            var uri = AssistSettings.WsUri(S.RelayUrl);
            if (statusBusy || uri == null || main == null || !main.IsVisible || S.Pcs.Count == 0 || snap) return;
            statusBusy = true; var codes = S.Pcs.Select(p => p.Code).ToList();
            new Thread(() =>
            {
                var r = RelayStatus.Query(uri, codes);
                ui.BeginInvoke(new Action(() => { statusBusy = false; PcStatus = r ?? new Dictionary<string, string>(); if (main != null) main.ShowPcStatus(); }));
            }) { IsBackground = true, Name = "pc status" }.Start();
        }

        static DateTime Parse(string iso) { DateTime d; return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue; }
        void StartViewer(ViewerSession v, string code)
        {
            v.Clip = AssistSettings.TestDir != null ? (IClip)new FileClip(Path.Combine(AssistSettings.Dir, "clipboard.txt")) : new WpfClip(ui);
            v.AllowDirect = !S.DirectOff;
            v.Player = AssistSettings.TestDir != null ? (IAudioSink)new CountingSink() : new WaveOutSink();   // tests never make a sound
            v.Detail += t => Log(t);
            Viewer = v;
            v.Changed += () => ui.BeginInvoke(new Action(() => { if (main != null) main.RefreshHelpSomeone(); WriteStatus(); UpdateTrayText(); }));
            v.Chat += t => Log("chat from host: " + t);
            v.Note += t => Log(t);
            if (AssistSettings.Headless) v.Files.Offered += (n, s, a) => { Log("file offered: " + n); lock (pendingFiles) pendingFiles.Enqueue(a); };
            v.Changed += () => ui.BeginInvoke(new Action(() =>
            {
                var pc = S.FindPc(v.Code);   // v1.1: Your PCs remember when they were last reached
                if (pc != null && v.State == ViewerState.Connected && (DateTime.Now - Parse(pc.LastUsed)).TotalMinutes > 1) { pc.LastUsed = DateTime.Now.ToString("o"); S.Save(); }
                // v1.4: one of Your PCs told us its network adapter - kept for Wake-on-LAN
                if (pc != null && v.HostUnattended && v.HostMac != null && pc.Mac != v.HostMac) { pc.Mac = v.HostMac; S.Save(); Log("learned the network adapter of " + pc.Name); if (main != null) main.RefreshHelpSomeone(); }
            }));
            v.Ended += r => ui.BeginInvoke(new Action(() =>
            {
                Log("viewer ended: " + r);
                // v1.1: a remembered key that no longer works (the unattended password was changed there) is forgotten
                if (v.Restarting && v.PasswordKey != null) BeginRestartWait(v);
                if (v.UsedSavedKey && v.WrongPassword) { var pc = S.FindPc(v.Code); if (pc != null) { pc.Key = null; S.Save(); if (main != null) main.Say("The remembered password for " + pc.Name + " didn't work any more, so it was forgotten. Type the password to connect.", Ui.Amber); } }
                if (main != null) main.RefreshHelpSomeone(); WriteStatus();
            }));
            if (!AssistSettings.Headless) { if (viewerWin != null && viewerWin.IsVisible && restartWait == null) { var old = viewerWin; viewerWin = null; old.Close(); } viewerWin = new ViewerWindow(v, code); viewerWin.Show(); }
            v.Start();
            if (main != null) main.RefreshHelpSomeone();
        }
        public void ShowViewer() { if (viewerWin != null) { viewerWin.Show(); viewerWin.Activate(); } }

        // ---------------- the panic hotkey: Ctrl+Alt+End disconnects while someone is connected (or asking)
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        bool hotkeyRegistered;
        void RegisterHotkey()
        {
            if (hotkeyRegistered || hotkeyWin == null) return;
            hotkeyRegistered = true;
            if (AssistSettings.TestDir != null) { hotkeyOk = true; return; }   // tests: the same path is driven by posting WM_HOTKEY, nothing global is taken
            hotkeyOk = RegisterHotKey(hotkeyWin.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, 0x23);   // Alt+Ctrl, no repeat, End
        }
        void UnregisterHotkey() { if (!hotkeyRegistered) return; hotkeyRegistered = false; if (AssistSettings.TestDir == null && hotkeyWin != null) UnregisterHotKey(hotkeyWin.Handle, HotkeyId); }
        public int Panics;
        IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId) { handled = true; Panics++; Log("panic hotkey"); if (hotkeyRegistered) StopHost("You pressed Ctrl+Alt+End."); }
            return IntPtr.Zero;
        }

        // ---------------- tray, the Hallow icon, accent, updates, crash notice
        void SetupTray()
        {
            tray = new WF.NotifyIcon { Text = "Hallow Assist", Visible = true };
            try { tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new WF.ContextMenuStrip();
            var open = new WF.ToolStripMenuItem("Open Hallow Assist"); open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold); open.Click += (s, e) => ShowMain(); menu.Items.Add(open);
            var disc = new WF.ToolStripMenuItem("Disconnect"); disc.Click += (s, e) => Invoke("disconnect"); menu.Items.Add(disc);
            menu.Opening += (s, e) => disc.Enabled = SessionActive();
            menu.Items.Add(new WF.ToolStripSeparator());
            var quit = new WF.ToolStripMenuItem("Quit"); quit.Click += (s, e) => Quit(); menu.Items.Add(quit);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) ShowMain(); };
        }
        void UpdateTrayText()
        {
            if (tray == null) return;
            var h = host; var t = "Hallow Assist";
            if (h != null && h.State == HostState.Connected) t = "Hallow Assist - " + h.HelperName + " is connected";
            else if (Viewer != null && Viewer.State == ViewerState.Connected) t = "Hallow Assist - helping someone";
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }
        bool SessionActive() { return (host != null && host.State != HostState.Ended && host.State != HostState.Starting && (!host.Unattended || host.State == HostState.Connected)) || (Viewer != null && Viewer.State != ViewerState.Ended); }
        public void CheckHost()
        {
            if (tray == null) return;
            if (!S.UseHallowIcon) { SetHosted(false); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool on = false;
                try { var r = Family.Send("Hub", "tray-host", 1500); if (r != null && r.StartsWith("ok {")) { var d = Msg.FromJson(r.Substring(3)); on = Msg.Bool(d, "on"); } } catch { }
                ui.BeginInvoke(new Action(() => SetHosted(on && S.UseHallowIcon)));
            });
        }
        bool balloonOwn;
        void SetHosted(bool on) { hubHosts = on; if (tray != null && !balloonOwn) tray.Visible = !on; }
        void Notify(string title, string text)
        {
            if (tray == null || AssistSettings.TestDir != null) return;
            if (!hubHosts) { tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Family.Error(Family.Send("Hub", "notify Assist\t" + title + "\t" + text, 2000), "no answer") == null) return;
                ui.BeginInvoke(new Action(() => { balloonOwn = true; tray.Visible = true; tray.ShowBalloonTip(4000, title, text, WF.ToolTipIcon.None); var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) }; t.Tick += (s, e) => { t.Stop(); balloonOwn = false; SetHosted(hubHosts); }; t.Start(); }));
            });
        }
        FileSystemWatcher accentWatch;
        public void ApplyAccent()
        {
            var a = Accent.Current(S.UseHallowAccent); if (a == Program.AccentHex) return;
            Program.LoadTheme(Application.Current);
            if (main != null) main.Build();
        }
        GhRelease pending;
        public void CheckForUpdate(bool manual)
        {
            if (manual && main != null) main.Say("Checking GitHub for a newer Hallow Assist...");
            new Thread(() =>
            {
                var r = Updater.Latest();
                ui.BeginInvoke(new Action(() =>
                {
                    S.LastUpdateCheck = DateTime.Now.ToString("o"); S.Save();
                    if (r == null) { if (manual && main != null) main.Say("Couldn't reach GitHub - try again later.", Ui.Amber); return; }
                    if (!Updater.IsNewer(r)) { if (manual && main != null) main.Say("You have the newest version (v" + Program.Version + ").", Ui.Green); return; }
                    pending = r;
                    if (main != null) { main.UpdateBtn.Content = "Update to v" + r.Version.ToString(2); main.UpdateBtn.Visibility = Visibility.Visible; main.Say("Hallow Assist v" + r.Version.ToString(2) + " is out - click Update at the top.", Ui.Green); }
                }));
            }) { IsBackground = true }.Start();
        }
        public void InstallUpdate()
        {
            if (pending == null) return;
            if (SessionActive()) { MessageBox.Show(main, "Finish the session first - updating restarts Hallow Assist.", "Hallow Assist"); return; }
            if (MessageBox.Show(main, "Install Hallow Assist v" + pending.Version.ToString(2) + " now?\n\nIt downloads from GitHub, checks the file and puts the new version in place. Your settings stay.", "Update Hallow Assist", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var r = pending; main.UpdateBtn.IsEnabled = false; main.UpdateBtn.Content = "Updating...";
            new Thread(() => { var err = Updater.Install(r); ui.BeginInvoke(new Action(() => { if (err != null) { main.Say(err, Ui.Amber); main.UpdateBtn.IsEnabled = true; main.UpdateBtn.Content = "Update to v" + r.Version.ToString(2); } else main.Say("Installing the update..."); })); }) { IsBackground = true }.Start();
        }
        public void CheckRelay(Action<string> done)
        {
            var web = AssistSettings.WebUrl(S.RelayUrl); if (web == null) { done("No relay set."); return; }
            new Thread(() =>
            {
                try
                {
                    System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                    using (var wc = new System.Net.WebClient()) { wc.Headers["User-Agent"] = "HallowAssist/" + Program.Version; var j = Msg.FromJson(wc.DownloadString(web + "healthz")); done(Msg.Bool(j, "ok") ? "The relay answers (version " + Msg.Str(j, "version") + ")." : "Something answered, but it isn't a Hallow Assist relay."); }
                }
                catch (Exception e) { done("Couldn't reach the relay: " + e.Message); }
            }) { IsBackground = true }.Start();
        }
        void ShowCrashNotice()
        {
            var f = Crash.Last(); if (f == null) return;
            var name = Path.GetFileName(f); if (name == S.LastCrashShown) return;
            S.LastCrashShown = name; S.Save();
            CrashNotice.Make(f).Show();
        }

        // ---------------- family link: status.json and the pipe
        public void WriteStatus(bool running = true)
        {
            var d = new Dictionary<string, object> {
                { "app", "Assist" }, { "version", Program.Version }, { "updated", Family.Time(DateTime.Now) }, { "running", running },
                { "session", host == null || host.State == HostState.Ended || (host.Unattended && host.State != HostState.Connected && host.State != HostState.Asking) ? "none" : host.State == HostState.Connected ? "connected" : host.State == HostState.Asking ? "asking" : "waiting" },
                { "helping", Viewer != null && Viewer.State != ViewerState.Ended },
                { "unattended", S.Unattended }, { "relaySet", AssistSettings.WsUri(S.RelayUrl) != null },
                { "lastCrash", Crash.LastTime() == null ? null : Family.Time(Crash.LastTime().Value) } };
            if (AssistSettings.TestDir != null) d["test"] = TestState();
            Family.WriteStatus(Family.Json(d));
        }
        Dictionary<string, object> TestState()
        {
            var t = new Dictionary<string, object>();
            var h = host;
            if (h != null) { t["host"] = h.State.ToString(); t["helper"] = h.HelperName; t["control"] = h.Control; t["clipboard"] = h.Clipboard; t["safety"] = h.Safety; t["framesSent"] = h.FramesSent; t["wrong"] = h.WrongPasswords; t["endReason"] = h.EndReason; t["unattendedHost"] = h.Unattended; t["inputEvents"] = h.InputEvents; t["screen"] = h.ScreenKind; t["direct"] = h.Direct; t["drawStrokes"] = DrawStrokes; t["soundShared"] = h.SoundShared; t["soundFlowing"] = h.SoundFlowing; t["audioSent"] = h.AudioPackets; }
            t["barText"] = h != null && h.State == HostState.Connected ? string.Join(" | ", HelpBar.Lines(h.HelperName, h.Control, h.Clipboard, h.Unattended, h.Safety, hotkeyOk, h.SoundShared)) : null; t["wake"] = WakeState;
            t["pcs"] = S.Pcs.Select(p => new Dictionary<string, object> { { "code", p.Code }, { "name", p.Name }, { "mac", p.Mac } }).ToArray();
            t["consentPending"] = pendingConsent != null; t["bar"] = barVisible; t["hotkey"] = hotkeyRegistered; t["panics"] = Panics; t["startsWithWindows"] = StartsWithWindows();
            lock (pendingFiles) t["filesPending"] = pendingFiles.Count;
            var v = Viewer;
            if (v != null) { t["viewer"] = v.State.ToString(); t["viewerSafety"] = v.Safety; t["frames"] = v.Frames; t["controlAllowed"] = v.ControlAllowed; t["clipboardAllowed"] = v.ClipboardAllowed; t["banner"] = v.Banner; t["viewerEnd"] = v.EndReason; t["monitors"] = v.Monitors.Length; t["monitor"] = v.Monitor; t["lastClip"] = v.LastClipFromThem; t["screenW"] = v.Screen.W; t["screenH"] = v.Screen.H; t["viewerDirect"] = v.Direct;
                t["viewerSound"] = v.SoundOn; t["hostSharesSound"] = v.HostSharesSound; t["audioPackets"] = v.AudioPackets; t["hostMac"] = v.HostMac; t["pathNote"] = v.PathNote;
                var cs = v.Player as CountingSink; if (cs != null) { t["audioSamples"] = cs.Samples; t["audioHz"] = cs.Hz; } }
            lock (log) t["log"] = log.ToArray();
            return t;
        }
        string Command(string line)
        {
            int sp = line.IndexOf(' '); var cmd = sp < 0 ? line : line.Substring(0, sp); var arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            switch (cmd)
            {
                case "ping": return "ok " + Family.Json(new Dictionary<string, object> { { "app", "Assist" }, { "version", Program.Version } });
                case "status": return OnUi(() => { WriteStatus(); return "ok " + File.ReadAllText(Path.Combine(AssistSettings.Dir, "status.json")); });
                case "open": return OnUi(() => { ShowMain(); return "ok"; });
                case "menu": return OnUi(() => "ok " + Family.Json(new Dictionary<string, object> { { "app", "Assist" }, { "items", MenuItems() } }));
                case "invoke": return OnUi(() => Invoke(arg));
                case "disconnect": return OnUi(() => Invoke("disconnect"));
                case "quit": new Thread(() => { Thread.Sleep(300); ui.BeginInvoke(new Action(Quit)); }) { IsBackground = true }.Start(); return "ok";
            }
            if (AssistSettings.TestDir != null && cmd.StartsWith("test-")) return OnUi(() => TestCommand(cmd, arg));
            return "error unknown command";
        }
        string OnUi(Func<string> f)
        {
            string r = "error no answer in time";
            var op = ui.BeginInvoke(new Action(() => { try { r = f(); } catch (Exception e) { r = "error " + Family.OneLine(e.Message); } }));
            return op.Wait(TimeSpan.FromSeconds(5)) == DispatcherOperationStatus.Completed ? r : "error no answer in time";
        }
        List<object> MenuItems()
        {
            return new List<object> {
                new Dictionary<string, object> { { "id", "gethelp" }, { "label", "Get help..." } },
                new Dictionary<string, object> { { "id", "helpsomeone" }, { "label", "Help someone..." } },
                new Dictionary<string, object> { { "id", "-" } },
                new Dictionary<string, object> { { "id", "disconnect" }, { "label", "Disconnect" }, { "enabled", SessionActive() } } };
        }
        // menu items only open the window: the person still clicks Get help themselves
        string Invoke(string id)
        {
            switch (id)
            {
                case "gethelp": case "helpsomeone": ShowMain(); return "ok";
                case "disconnect":
                    if (!SessionActive()) return "error No session";
                    if (host != null && host.State != HostState.Ended) StopHost("Disconnected from the menu.");
                    if (Viewer != null && Viewer.State != ViewerState.Ended) Viewer.End("You ended the session.", true);
                    return "ok";
            }
            return "error unknown item";
        }

        // developer tests only (HALLOW_ASSIST_TESTDIR): what a person would click, so tests never need windows
        string TestCommand(string cmd, string arg)
        {
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (cmd.StartsWith("test-view-") && Viewer == null) return "error no viewer";
            switch (cmd)
            {
                case "test-gethelp": GetHelp(); return "ok";
                case "test-direct": { var on = a.Length > 0 && a[0] == "on"; DirectNet.TestLoopback = on; S.DirectOff = !on; S.Save(); return "ok"; }   // on = direct over 127.0.0.1, anything else = relay only
                case "test-view-draw": { var p = a.Select(x => int.Parse(x)).ToArray(); Viewer.SendDraw(4242, p); return "ok"; }
                case "test-code": return host == null || host.Code == null ? "error no code yet" : "ok " + Family.Json(new Dictionary<string, object> { { "code", host.Code }, { "password", host.Password } });
                case "test-consent":
                    {
                        var p = pendingConsent; if (p == null) return "error nothing to answer";
                        pendingConsent = null;
                        p(new ConsentAnswer { Allow = a.Length > 0 && a[0] == "allow", Control = a.Contains("control"), Clipboard = a.Contains("clipboard") });
                        return "ok";
                    }
                case "test-hotkey": PostMessage(hotkeyWin.Handle, 0x0312, new IntPtr(HotkeyId), IntPtr.Zero); return "ok";
                case "test-control": if (host == null) return "error no session"; host.SetControl(a.Length > 0 && a[0] == "on"); return "ok";
                case "test-clipboard": if (host == null) return "error no session"; host.SetClipboard(a.Length > 0 && a[0] == "on"); return "ok";
                case "test-host-chat": if (host == null) return "error no session"; host.SendChat(arg); return "ok";
                case "test-host-file": if (host == null) return "error no session"; return host.SendFile(arg) ?? "ok";
                case "test-file-answer": { Action<bool> f; lock (pendingFiles) f = pendingFiles.Count > 0 ? pendingFiles.Dequeue() : null; if (f == null) return "error nothing to answer"; f(a.Length > 0 && a[0] == "accept"); return "ok"; }
                case "test-connect": if (a.Length < 3) return "error test-connect <code> <password> <name>"; Connect(a[0], a[1], string.Join(" ", a.Skip(2))); return "ok";
                case "test-view-move": Viewer.Move(int.Parse(a[0]), int.Parse(a[1])); return "ok";
                case "test-view-click": Viewer.Button(0, true, int.Parse(a[0]), int.Parse(a[1])); Viewer.Button(0, false, int.Parse(a[0]), int.Parse(a[1])); return "ok";
                case "test-view-key": Viewer.Key(int.Parse(a[0]), true); Viewer.Key(int.Parse(a[0]), false); return "ok";
                case "test-view-keydown": Viewer.Key(int.Parse(a[0]), true); return "ok";
                case "test-view-text": Viewer.Text(arg); return "ok";
                case "test-view-monitor": Viewer.SetMonitor(int.Parse(a[0])); return "ok";
                case "test-view-chat": Viewer.SendChat(arg); return "ok";
                case "test-view-clipsync": Viewer.ClipSync = a.Length > 0 && a[0] == "on"; return "ok";
                case "test-view-clip": Viewer.SendClip(arg); return "ok";
                case "test-view-file": return Viewer.SendFile(arg) ?? "ok";
                case "test-view-end": Viewer.End("You ended the session.", true); return "ok";
                case "test-view-hash":
                    lock (Viewer.ScreenLock) { using (var sha = System.Security.Cryptography.SHA256.Create()) return "ok " + Family.Json(new Dictionary<string, object> { { "w", Viewer.Screen.W }, { "h", Viewer.Screen.H }, { "mean", Mean(Viewer.Screen.Pixels) } }); }
                case "test-unattended-on": EnableUnattended(arg); return "ok " + Family.Json(new Dictionary<string, object> { { "code", S.UnattendedCode } });
                case "test-unattended-off": SetUnattended(false); return "ok";
                case "test-close": Quit(); return "ok";
                case "test-host-end": if (host != null) host.End("test", false); return "ok";
                // v1.4
                case "test-host-sound": if (host == null) return "error no session"; host.SetSound(a.Length > 0 && a[0] == "on"); return "ok";
                case "test-sound-unattended": S.SoundUnattended = a.Length > 0 && a[0] == "on"; S.Save(); return "ok";
                case "test-view-sound": Viewer.SetSound(a.Length > 0 && a[0] == "on"); return "ok";
                case "test-view-cut-direct": Viewer.CutDirect("test: the direct link was cut"); return "ok";
                case "test-host-cut-direct": if (host == null) return "error no session"; host.CutDirect("test: the direct link was cut"); return "ok";
                case "test-savepc": if (a.Length < 2) return "error test-savepc <code> <name>"; SavePc(string.Join(" ", a.Skip(1)), a[0], null); return "ok";
                case "test-wake": { var pc = S.FindPc(arg); if (pc == null) return "error no such PC"; if (Wol.ParseMac(pc.Mac) == null) return "error no MAC yet"; WakeState = "sending"; Wake(pc); return "ok"; }
            }
            return "error unknown test command";
        }
        static double Mean(byte[] p) { if (p.Length == 0) return 0; long s = 0; for (int i = 0; i < p.Length; i += 4) s += p[i] + p[i + 1] + p[i + 2]; return s / (p.Length / 4 * 3.0); }
    }

    // the real clipboard (on the UI thread, a few tries because other apps hold it briefly)
    public class WpfClip : IClip
    {
        readonly Dispatcher d; public WpfClip(Dispatcher d) { this.d = d; }
        public string Get() { string r = null; try { d.Invoke(new Action(() => { try { if (Clipboard.ContainsText()) r = Clipboard.GetText(); } catch { } }), TimeSpan.FromSeconds(2)); } catch { } return r; }
        public void Set(string t) { try { d.BeginInvoke(new Action(() => { for (int i = 0; i < 5; i++) { try { Clipboard.SetText(t); return; } catch { Thread.Sleep(60); } } })); } catch { } }
    }
    // developer tests: a file stands in for the clipboard
    public class FileClip : IClip
    {
        readonly string f; public FileClip(string file) { f = file; }
        public string Get() { try { return File.Exists(f) ? File.ReadAllText(f) : ""; } catch { return null; } }
        public void Set(string t) { try { File.WriteAllText(f, t); } catch { } }
    }
}

// Hallow Assist - the small windows: the consent question, the always-on-top "can see your screen" bar, file prompts,
// chat, the unattended-access setup and the crash notice.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HallowAssist
{
    // "Dom wants to see your screen" - Allow / Deny. Deny is the default; no answer in 60 s = Deny.
    public class ConsentWindow : Window
    {
        public ConsentAnswer Result = new ConsentAnswer();
        readonly CheckBox control, clip; readonly TextBlock countdown; int left = 60; DispatcherTimer timer;
        public event Action<ConsentAnswer> Answered;
        bool done;
        public ConsentWindow(string helper, string safety, bool browser)
        {
            Ui.Dress(this, "Hallow Assist - someone wants to help", 470);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.Header("ASSIST", 18)); sp.Children[0].SetValue(MarginProperty, new Thickness(0, 0, 0, 14));
            sp.Children.Add(Ui.T(helper + " wants to see your screen", 18, true, null, 6));
            sp.Children.Add(Ui.Muted("Only say yes if you know " + helper + ", you asked them for help, and you gave them the code and password yourself. " +
                "Nothing is shared until you click Allow, and you can stop it any time." + (browser ? " They're using a web browser." : ""), 12.5, 12));
            var sb = new Border { Background = Ui.R("PanelHi"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12) };
            var sr = new DockPanel();
            var code = new TextBlock { Text = safety, FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) }; DockPanel.SetDock(code, Dock.Left);
            sr.Children.Add(code);
            sr.Children.Add(Ui.Muted("Safety code - ask " + helper + " to read theirs out. If it's different, click Deny.", 12));
            sb.Child = sr; sp.Children.Add(sb);
            control = Ui.Switch("Let them use my mouse and keyboard", true, v => { });
            clip = Ui.Switch("Share copied text (clipboard) both ways", false, v => { });
            sp.Children.Add(control); sp.Children.Add(clip);
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            countdown = Ui.Muted("", 11.5); countdown.VerticalAlignment = VerticalAlignment.Center;
            var deny = Ui.Btn("Deny", () => Finish(false)); deny.IsCancel = true; deny.IsDefault = true; deny.MinWidth = 90;
            var allow = Ui.Btn("Allow", () => Finish(true), true); allow.MinWidth = 90; allow.Margin = new Thickness(0);
            var btns = Ui.Row(deny, allow); DockPanel.SetDock(btns, Dock.Right);
            row.Children.Add(btns); row.Children.Add(countdown);
            sp.Children.Add(row);
            Content = sp;
            Tick();
            if (!Snap.On) { timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (s, e) => { left--; Tick(); if (left <= 0) Finish(false); }; timer.Start(); }
            Closing += (s, e) => { if (!done) { done = true; Fire(new ConsentAnswer()); } };
            Loaded += (s, e) => deny.Focus();
        }
        void Tick() { countdown.Text = "Closes as Deny in " + left + " s"; }
        void Finish(bool allow)
        {
            if (done) return; done = true;
            if (timer != null) timer.Stop();
            Fire(new ConsentAnswer { Allow = allow, Control = allow && control.IsChecked == true, Clipboard = allow && clip.IsChecked == true });
            Close();
        }
        void Fire(ConsentAnswer a) { Result = a; var h = Answered; if (h != null) h(a); }
        public void Cancel() { done = true; if (timer != null) timer.Stop(); Close(); }
    }

    // While someone is connected: always on top, can't be minimized or closed away (closing it = Disconnect).
    public class HelpBar : Window
    {
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        readonly TextBlock text, sub; readonly Button controlBtn, clipBtn;
        public Action Disconnect, ToggleControl, ToggleClipboard, OpenChat, SendFile;
        bool allowClose; DispatcherTimer topTimer;
        public HelpBar()
        {
            Ui.Dress(this, "Hallow Assist - someone can see your screen", 0);
            Width = double.NaN; SizeToContent = SizeToContent.WidthAndHeight; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = true; ShowActivated = false;
            AllowsTransparency = false; BorderThickness = new Thickness(0);
            var root = new Border { Background = Ui.B("#2A1A20"), BorderBrush = Ui.Red, BorderThickness = new Thickness(2), Padding = new Thickness(12, 8, 10, 8) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new System.Windows.Shapes.Ellipse { Width = 11, Height = 11, Fill = Ui.Red, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            row.Children.Add(dot);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            text = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
            sub = new TextBlock { FontSize = 11, Foreground = Ui.R("Muted") };
            texts.Children.Add(text); texts.Children.Add(sub); row.Children.Add(texts);
            controlBtn = Ui.Btn("Stop control", () => { if (ToggleControl != null) ToggleControl(); });
            clipBtn = Ui.Btn("Share clipboard", () => { if (ToggleClipboard != null) ToggleClipboard(); });
            row.Children.Add(controlBtn); row.Children.Add(clipBtn);
            row.Children.Add(Ui.Btn("Chat", () => { if (OpenChat != null) OpenChat(); }));
            row.Children.Add(Ui.Btn("Send a file", () => { if (SendFile != null) SendFile(); }));
            var disc = Ui.Btn("Disconnect", () => { if (Disconnect != null) Disconnect(); }, true); disc.Margin = new Thickness(0);
            row.Children.Add(disc);
            root.Child = row; Content = root;
            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };
            StateChanged += (s, e) => { if (WindowState != WindowState.Normal) WindowState = WindowState.Normal; };   // never minimized
            Closing += (s, e) => { if (!allowClose) { e.Cancel = true; if (Disconnect != null) Disconnect(); } };   // Alt+F4 = Disconnect
            Loaded += (s, e) => { var wa = SystemParameters.WorkArea; Left = wa.Left + (wa.Width - ActualWidth) / 2; Top = wa.Top + 6; };
            if (!Snap.On)
            {
                topTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                topTimer.Tick += (s, e) => { try { var h = new WindowInteropHelper(this).Handle; if (h != IntPtr.Zero) SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0013); if (!IsVisible) Show(); } catch { } };   // HWND_TOPMOST, no move/size/activate
                topTimer.Start();
            }
        }
        public void Update(string helper, bool control, bool clipboard, bool unattended, string safety, bool hotkeyOk)
        {
            text.Text = helper + " can see your screen" + (control ? " and control it" : "") + (unattended ? " (unattended access)" : "");
            sub.Text = "Safety code " + safety + "  ·  " + (hotkeyOk ? "Ctrl+Alt+End disconnects" : "Use Disconnect to end it") + (clipboard ? "  ·  clipboard shared" : "");
            controlBtn.Content = control ? "Stop control" : "Allow control";
            clipBtn.Content = clipboard ? "Stop clipboard" : "Share clipboard";
        }
        public void CloseForGood() { allowClose = true; if (topTimer != null) topTimer.Stop(); Close(); }
    }

    // "X wants to send you a file" (both sides)
    public class PromptWindow : Window
    {
        public PromptWindow(string title, string text, string yes, string no, Action<bool> answer)
        {
            Ui.Dress(this, "Hallow Assist", 420);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T(title, 15, true, null, 6));
            sp.Children.Add(Ui.Muted(text, 12.5, 14));
            bool done = false;
            Action<bool> fin = ok => { if (done) return; done = true; answer(ok); Close(); };
            var n = Ui.Btn(no, () => fin(false)); n.IsCancel = true; n.IsDefault = true;
            var y = Ui.Btn(yes, () => fin(true), true); y.Margin = new Thickness(0);
            var row = Ui.Row(n, y); row.HorizontalAlignment = HorizontalAlignment.Right; sp.Children.Add(row);
            Content = sp;
            Closing += (s, e) => { if (!done) { done = true; answer(false); } };
        }
    }

    public class ChatWindow : Window
    {
        readonly StackPanel log; readonly ScrollViewer scroll; readonly TextBox box;
        public Action<string> Send;
        public ChatWindow(string other)
        {
            Ui.Dress(this, "Hallow Assist - chat with " + other, 380);
            SizeToContent = SizeToContent.Manual; Height = 420; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var dp = new DockPanel { Margin = new Thickness(14) };
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom);
            box = Ui.Field();
            var send = Ui.Btn("Send", Go, true); send.Margin = new Thickness(8, 0, 0, 0); send.IsDefault = true; DockPanel.SetDock(send, Dock.Right);
            bottom.Children.Add(send); bottom.Children.Add(box);
            dp.Children.Add(bottom);
            log = new StackPanel(); scroll = new ScrollViewer { Content = log, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            dp.Children.Add(scroll);
            Content = dp;
            Closing += (s, e) => { e.Cancel = true; Hide(); };
        }
        void Go() { var t = box.Text.Trim(); if (t.Length == 0) return; if (Send != null) Send(t); Add("You", t, true); box.Text = ""; }
        public void Add(string who, string text, bool me)
        {
            var tb = Ui.T(who + ": " + text, 12.5, false, me ? Ui.Accent : null, 4); log.Children.Add(tb); scroll.ScrollToEnd();
        }
        public void ForceClose() { Closing += (s, e) => e.Cancel = false; Close(); }
    }

    // turning on unattended access: a separate strong password, and Start with Windows
    public class UnattendedDialog : Window
    {
        public string Password;
        readonly PasswordBox p1, p2; readonly TextBlock err;
        public UnattendedDialog()
        {
            Ui.Dress(this, "Hallow Assist - unattended access", 480);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.T("Unattended access to this PC", 17, true, null, 6));
            sp.Children.Add(Ui.Muted("Only for your own PCs. With it on, you can connect to this PC from your other PC, phone or TV browser without anyone clicking Allow here. " +
                "Hallow Assist starts with Windows and waits for you; the red bar still shows while you're connected, and Ctrl+Alt+End or Disconnect still end it.", 12.5, 12));
            sp.Children.Add(Ui.T("Pick a password just for this (at least 12 characters, not one you use anywhere else):", 12.5, false, null, 6));
            p1 = new PasswordBox { Height = 32, Padding = new Thickness(8, 6, 8, 6), Background = Ui.B("#1A1522"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), Margin = new Thickness(0, 0, 0, 8) };
            p2 = new PasswordBox { Height = 32, Padding = new Thickness(8, 6, 8, 6), Background = Ui.B("#1A1522"), Foreground = Ui.R("Text"), BorderBrush = Ui.R("Line"), Margin = new Thickness(0, 0, 0, 8) };
            sp.Children.Add(p1); sp.Children.Add(Ui.Muted("Again:", 12, 4)); sp.Children.Add(p2);
            err = Ui.T("", 12, false, Ui.Amber, 8); sp.Children.Add(err);
            sp.Children.Add(Ui.Muted("The password is never stored - only a key made from it, encrypted for your Windows account.", 11.5, 12));
            var cancel = Ui.Btn("Cancel", () => { DialogResult = false; }); cancel.IsCancel = true;
            var ok = Ui.Btn("Turn on", () => { var e = Check(p1.Password, p2.Password); if (e != null) { err.Text = e; return; } Password = p1.Password; DialogResult = true; }, true); ok.Margin = new Thickness(0);
            var row = Ui.Row(cancel, ok); row.HorizontalAlignment = HorizontalAlignment.Right; sp.Children.Add(row);
            Content = sp;
        }
        public static string Check(string a, string b)
        {
            if (a != b) return "The two passwords are different.";
            if (a.Length < 12) return "Use at least 12 characters.";
            int kinds = 0; bool lo = false, up = false, dg = false, ot = false;
            foreach (var c in a) { if (char.IsLower(c)) lo = true; else if (char.IsUpper(c)) up = true; else if (char.IsDigit(c)) dg = true; else ot = true; }
            kinds = (lo ? 1 : 0) + (up ? 1 : 0) + (dg ? 1 : 0) + (ot ? 1 : 0);
            if (kinds < 3 && a.Length < 20) return "Mix at least three of: lower case, upper case, digits, symbols (or use 20+ characters).";
            var distinct = new HashSet<char>(a).Count; if (distinct < 6) return "That password repeats itself too much.";
            return null;
        }
        public void SnapFill(string e) { err.Text = e; }
    }

    public static class CrashNotice
    {
        public static Window Make(string file)
        {
            var w = new Window(); Ui.Dress(w, "Hallow Assist", 440);
            w.ResizeMode = ResizeMode.NoResize; w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true;
            var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
            sp.Children.Add(Ui.T("Hallow Assist closed unexpectedly last time", 15, true, null, 6));
            DateTime when = DateTime.Now; try { when = System.IO.File.GetLastWriteTime(file); } catch { }
            sp.Children.Add(Ui.Muted("On " + when.ToString("d MMM yyyy, HH:mm") + ". A report was saved on this PC - nothing was sent anywhere.", 12.5, 14));
            var row = new WrapPanel();
            row.Children.Add(Ui.Btn("Show details", () => { try { System.Diagnostics.Process.Start("notepad.exe", "\"" + file + "\""); } catch { } }));
            row.Children.Add(Ui.Btn("Open folder", () => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { } }));
            var ok = Ui.Btn("OK", () => w.Close(), true); ok.MinWidth = 70; row.Children.Add(ok);
            sp.Children.Add(row); w.Content = sp;
            return w;
        }
    }
}

// Hallow Assist "Help someone" window: their screen, their monitors, control (if they allowed it), chat, clipboard, files,
// quality and connection stats. Ctrl+Alt+Del can't be sent from a normal app - the button says so.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace HallowAssist
{
    public class ViewerWindow : Window
    {
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        readonly ViewerSession v;
        readonly Image img; readonly Border stage; readonly TextBlock status, banner, stats, safetyText; readonly StackPanel monitors;
        readonly ToggleButton controlBtn, clipBtn, drawBtn, soundBtn, recBtn; readonly Button sendCopiedBtn, getCopiedBtn; readonly ComboBox quality; readonly Border waitPanel; readonly Button restartBtn;
        // v1.2: drawing on their screen - strokes go out in small batches while you drag
        bool snapRec;
        bool drawOn, drawing; long strokeId = new Random().Next(1, 1 << 30); readonly System.Collections.Generic.List<int> drawPts = new System.Collections.Generic.List<int>(); DispatcherTimer drawFlush;
        WriteableBitmap wb; ChatWindow chat;
        bool controlOn = true; int pending; System.Drawing.Rectangle dirty = System.Drawing.Rectangle.Empty; readonly object dirtyLock = new object();
        long lastFrames, lastBytes; DateTime lastStats = DateTime.UtcNow;

        public ViewerWindow(ViewerSession session, string code)
        {
            v = session;
            Ui.Dress(this, "Hallow Assist - helping " + Ui.FormatCode(code), 1180);
            SizeToContent = SizeToContent.Manual; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var dp = new DockPanel();
            // toolbar
            var bar = new WrapPanel { Margin = new Thickness(10, 8, 10, 6) };
            monitors = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(monitors);
            controlBtn = new ToggleButton { Content = "Control", Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4), IsChecked = true, ToolTip = "Use their mouse and keyboard (when they allowed it)" };
            controlBtn.Click += (s, e) => { controlOn = controlBtn.IsChecked == true; };
            bar.Children.Add(controlBtn);
            clipBtn = new ToggleButton { Content = "Sync clipboard", Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Copied text goes both ways (only if they turned on clipboard sharing)" };
            clipBtn.Click += (s, e) => v.ClipSync = clipBtn.IsChecked == true;
            bar.Children.Add(clipBtn);
            // v1.4: their PC's sound - off until you turn it on, and only while they share it
            soundBtn = new ToggleButton { Content = "Sound", Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Hear their PC's sound. Off until you turn it on, and it only plays while they have Share sound on (their red bar says so)." };
            soundBtn.Click += (s, e) => v.SetSound(soundBtn.IsChecked == true);
            bar.Children.Add(soundBtn);
            drawBtn = new ToggleButton { Content = "Draw", Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4), ToolTip = "Draw on their screen to show them where to click (lines fade after a few seconds). Right-click clears. Works in view-only sessions too." };
            drawBtn.Click += (s, e) => { drawOn = drawBtn.IsChecked == true; img.Cursor = drawOn ? Cursors.Pen : Cursors.Arrow; if (!drawOn) EndStroke(); };
            bar.Children.Add(drawBtn);
            bar.Children.Add(Bb("Chat", OpenChat));
            bar.Children.Add(Bb("Send a file", PickFile));
            // v1.5: copied files both ways (only while they share their clipboard; the receiving side is asked first)
            sendCopiedBtn = Bb("Send the files I copied", () => Note(v.SendCopiedFiles() ?? ""));
            sendCopiedBtn.ToolTip = "Copy files in Explorer on this PC, then click here: they're asked, and once they accept, the files are on their clipboard - paste them into any folder there with Ctrl+V.";
            bar.Children.Add(sendCopiedBtn);
            getCopiedBtn = Bb("Copy their files here", () => Note(v.GetCopiedFiles()));
            getCopiedBtn.ToolTip = "Get the files they copied in Explorer on their PC: you're asked first, then the files are in your received-files folder and on your clipboard (paste with Ctrl+V).";
            bar.Children.Add(getCopiedBtn);
            // v1.5: recording on this PC (off until you click it; their red bar says Recording the whole time)
            recBtn = new ToggleButton { Content = "Record", Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4),
                ToolTip = "Record what you see (and hear, if they share sound) to " + v.RecFolder + " on this PC. Their red bar says Recording the whole time - it can't start until their PC confirms that - and it stops if they press Stop recording or the session ends." };
            recBtn.Click += (s, e) => { if (v.Rec == ViewerSession.RecState.Off) Note(v.StartRecording() ?? ""); else v.StopRecording("You stopped recording.", true); Refresh(); };
            bar.Children.Add(recBtn);
            quality = new ComboBox { Style = Ui.S("Combo"), Width = 130, Margin = new Thickness(0, 0, 6, 4), ToolTip = "Picture quality" };
            foreach (var q in new[] { "Auto quality", "Best picture", "Balanced", "Fastest" }) quality.Items.Add(new ComboBoxItem { Content = q });
            quality.SelectedIndex = 0;
            quality.SelectionChanged += (s, e) => v.SetQuality(new[] { "auto", "best", "balanced", "fast" }[Math.Max(0, quality.SelectedIndex)]);
            bar.Children.Add(quality);
            var cad = Bb("Ctrl+Alt+Del", null); cad.IsEnabled = false;
            ToolTipService.SetShowOnDisabled(cad, true);
            cad.ToolTip = "Ctrl+Alt+Del can't be sent: Windows only accepts it from their own keyboard. Ask them to press it.";
            bar.Children.Add(cad);
            restartBtn = Bb("Restart their PC", ConfirmRestart); restartBtn.Visibility = Visibility.Collapsed;
            restartBtn.ToolTip = "Your own PCs only (unattended access): Windows restarts and Hallow Assist connects again by itself when it's back.";
            bar.Children.Add(restartBtn);
            var end = Ui.Btn("Disconnect", () => v.End("You ended the session.", true), true); end.Margin = new Thickness(0, 0, 6, 4);
            bar.Children.Add(end);
            DockPanel.SetDock(bar, Dock.Top); dp.Children.Add(bar);
            banner = new TextBlock { Background = Ui.B("#3A2E14"), Foreground = Ui.Amber, Padding = new Thickness(12, 6, 12, 6), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            DockPanel.SetDock(banner, Dock.Top); dp.Children.Add(banner);
            var foot = new DockPanel { Margin = new Thickness(10, 4, 10, 6) };
            stats = Ui.Muted("", 11.5); DockPanel.SetDock(stats, Dock.Right); foot.Children.Add(stats);
            status = Ui.Muted("", 11.5); foot.Children.Add(status);
            DockPanel.SetDock(foot, Dock.Bottom); dp.Children.Add(foot);
            // the screen
            img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Arrow, Focusable = true };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var grid = new Grid();
            grid.Children.Add(img);
            var wp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
            wp.Children.Add(Ui.T("Waiting for them to click Allow...", 17, true, null, 10));
            var sb = new Border { Background = Ui.R("PanelHi"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10) };
            var sr = new DockPanel();
            safetyText = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Ui.Accent, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(safetyText, Dock.Left);
            sr.Children.Add(safetyText); sr.Children.Add(Ui.Muted("Safety code - read it out; they should see the same 4 digits. Different digits = hang up.", 12));
            sb.Child = sr; wp.Children.Add(sb);
            wp.Children.Add(Ui.Muted("Everything is end-to-end encrypted - your relay only passes sealed messages along.", 12));
            waitPanel = new Border { Child = wp, Background = Ui.R("Panel"), CornerRadius = new CornerRadius(14), Padding = new Thickness(22), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(waitPanel);
            stage = new Border { Background = Ui.B("#0C0A10"), Child = grid, Focusable = true };
            dp.Children.Add(stage);
            Content = dp;

            drawFlush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) }; drawFlush.Tick += (s, e) => FlushStroke();
            img.MouseMove += (s, e) => { if (drawOn) { if (drawing) { int dx, dy; Norm(e, out dx, out dy); drawPts.Add(dx); drawPts.Add(dy); } return; } if (Ok()) { int nx, ny; Norm(e, out nx, out ny); v.Move(nx, ny); } };
            img.MouseDown += (s, e) =>
            {
                img.Focus();
                if (drawOn)
                {
                    if (e.ChangedButton == MouseButton.Right) { v.ClearDraw(); e.Handled = true; return; }
                    if (e.ChangedButton != MouseButton.Left) return;
                    int dx, dy; Norm(e, out dx, out dy); strokeId++; drawing = true; drawPts.Clear(); drawPts.Add(dx); drawPts.Add(dy);
                    img.CaptureMouse(); drawFlush.Start(); e.Handled = true; return;
                }
                if (!Ok()) return; int nx, ny; Norm(e, out nx, out ny); img.CaptureMouse(); v.Button(Btn(e.ChangedButton), true, nx, ny); e.Handled = true; };
            img.MouseUp += (s, e) => { if (drawOn) { if (drawing && e.ChangedButton == MouseButton.Left) { EndStroke(); img.ReleaseMouseCapture(); } e.Handled = true; return; } if (!Ok()) return; int nx, ny; Norm(e, out nx, out ny); v.Button(Btn(e.ChangedButton), false, nx, ny); img.ReleaseMouseCapture(); e.Handled = true; };
            img.MouseWheel += (s, e) => { if (Ok()) v.Wheel(e.Delta, 0); };
            PreviewKeyDown += (s, e) => Key(e, true);
            PreviewKeyUp += (s, e) => Key(e, false);
            Deactivated += (s, e) => ReleaseKeys();
            AllowDrop = true;
            Drop += (s, e) => { var f = e.Data.GetData(DataFormats.FileDrop) as string[]; if (f != null) foreach (var p in f) Note(v.SendFile(p)); };
            Closing += (s, e) => { if (v.State != ViewerState.Ended) v.End("You ended the session.", true); if (chat != null) chat.ForceClose(); };

            v.Changed += () => Dispatcher.BeginInvoke(new Action(Refresh));
            v.Painted += r => { lock (dirtyLock) dirty = dirty.IsEmpty ? r : System.Drawing.Rectangle.Union(dirty, r); if (System.Threading.Interlocked.Exchange(ref pending, 1) == 0) Dispatcher.BeginInvoke(new Action(Paint), DispatcherPriority.Render); };
            v.Chat += t => Dispatcher.BeginInvoke(new Action(() => { OpenChat(); chat.Add("Them", t, false); }));
            v.Note += t => Dispatcher.BeginInvoke(new Action(() => Note(t)));
            v.Files.Offered += (name, size, answer) => Dispatcher.BeginInvoke(new Action(() => new PromptWindow("They want to send you a file", name + " (" + Transfers.Size(size) + "). It goes into " + v.Files.Folder + "." + Transfers.Warning(name), "Accept", "Decline", answer) { Owner = this }.Show()));
            v.Files.OfferedMany += (names, total, clip, answer) => Dispatcher.BeginInvoke(new Action(() => new PromptWindow(Transfers.BatchTitle("They", names.Length, clip), Transfers.BatchText(names, total, v.Files.Folder, clip && v.ClipboardAllowed), "Accept", "Decline", answer) { Owner = this }.Show()));
            if (!Snap.On) { var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; t.Tick += (s, e) => ShowStats(); t.Start(); }
            Refresh();
        }
        void FlushStroke() { if (drawPts.Count >= 2) { v.SendDraw(strokeId, drawPts.ToArray()); var lx = drawPts[drawPts.Count - 2]; var ly = drawPts[drawPts.Count - 1]; drawPts.Clear(); if (drawing) { drawPts.Add(lx); drawPts.Add(ly); } } }
        void EndStroke() { if (!drawing) return; FlushStroke(); drawing = false; drawPts.Clear(); drawFlush.Stop(); }
        void ConfirmRestart()
        {
            if (!v.CanRestart) return;
            var r = MessageBox.Show(this, "Restart this PC now?\n\nWindows restarts in 10 seconds (unsaved work there is lost). Hallow Assist connects again by itself when it's back - " +
                "if Windows asks someone to sign in first, it connects after that.", "Hallow Assist - restart their PC", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes) v.RequestRestart();
        }
        Button Bb(string label, Action a) { var b = Ui.Btn(label, a); b.Margin = new Thickness(0, 0, 6, 4); return b; }
        bool Ok() { return controlOn && v.CanControl; }
        static int Btn(MouseButton b) { return b == MouseButton.Right ? 1 : b == MouseButton.Middle ? 2 : 0; }
        void Norm(MouseEventArgs e, out int nx, out int ny)
        {
            var p = e.GetPosition(img);
            nx = (int)Math.Round(Math.Max(0, Math.Min(1, p.X / Math.Max(1, img.ActualWidth))) * 65535);
            ny = (int)Math.Round(Math.Max(0, Math.Min(1, p.Y / Math.Max(1, img.ActualHeight))) * 65535);
        }
        readonly System.Collections.Generic.HashSet<int> held = new System.Collections.Generic.HashSet<int>();
        void Key(KeyEventArgs e, bool down)
        {
            if (!(img.IsKeyboardFocused || stage.IsKeyboardFocused) || !Ok()) return;
            var k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            int vk = KeyInterop.VirtualKeyFromKey(k); if (vk == 0) return;
            int scan = (int)MapVirtualKey((uint)vk, 4);   // MAPVK_VK_TO_VSC_EX: 0xE0xx for extended keys
            if (scan == 0) return;
            if ((scan & 0xFF00) == 0xE100) return;
            if (vk == 0x2D || vk == 0x2E || (vk >= 0x21 && vk <= 0x28) || vk == 0x5B || vk == 0x5C || vk == 0x5D || vk == 0x6F || vk == 0xA3 || vk == 0xA5) scan = 0xE000 | (scan & 0xFF);
            if (down) held.Add(scan); else held.Remove(scan);
            v.Key(scan, down); e.Handled = true;
        }
        void ReleaseKeys() { foreach (var s in held.ToArray()) v.Key(s, false); held.Clear(); }

        void Paint()
        {
            System.Threading.Interlocked.Exchange(ref pending, 0);
            System.Drawing.Rectangle r; lock (dirtyLock) { r = dirty; dirty = System.Drawing.Rectangle.Empty; }
            lock (v.ScreenLock)
            {
                var fb = v.Screen; if (fb.W == 0) return;
                if (wb == null || wb.PixelWidth != fb.W || wb.PixelHeight != fb.H) { wb = new WriteableBitmap(fb.W, fb.H, 96, 96, PixelFormats.Bgr32, null); img.Source = wb; r = new System.Drawing.Rectangle(0, 0, fb.W, fb.H); }
                r.Intersect(new System.Drawing.Rectangle(0, 0, fb.W, fb.H)); if (r.IsEmpty) return;
                wb.WritePixels(new Int32Rect(r.X, r.Y, r.Width, r.Height), fb.Pixels, fb.W * 4, (r.Y * fb.W + r.X) * 4);
            }
        }
        public void Refresh()
        {
            status.Text = v.Status + (v.Safety != null && v.State == ViewerState.Connected ? "  ·  safety code " + v.Safety : "");
            safetyText.Text = v.Safety ?? "----";
            waitPanel.Visibility = v.State == ViewerState.Connected ? Visibility.Collapsed : Visibility.Visible;
            banner.Text = v.Banner; banner.Visibility = string.IsNullOrEmpty(v.Banner) ? Visibility.Collapsed : Visibility.Visible;
            drawBtn.IsEnabled = v.State == ViewerState.Connected; if (!drawBtn.IsEnabled && drawOn) { drawOn = false; drawBtn.IsChecked = false; img.Cursor = Cursors.Arrow; EndStroke(); }
            restartBtn.Visibility = v.HostUnattended ? Visibility.Visible : Visibility.Collapsed; restartBtn.IsEnabled = v.CanRestart;
            controlBtn.IsEnabled = v.ControlAllowed; controlBtn.IsChecked = v.ControlAllowed && controlOn; controlBtn.Content = v.ControlAllowed ? "Control" : "View only";
            clipBtn.IsEnabled = v.ClipboardAllowed; if (!v.ClipboardAllowed) { clipBtn.IsChecked = false; v.ClipSync = false; }
            soundBtn.IsEnabled = v.State == ViewerState.Connected && v.HostHasSound; soundBtn.IsChecked = v.SoundOn;
            soundBtn.Content = v.SoundOn && !v.HostSharesSound ? "Sound (they haven't shared it)" : "Sound";
            sendCopiedBtn.IsEnabled = getCopiedBtn.IsEnabled = v.State == ViewerState.Connected && v.ClipboardAllowed;
            var rec = snapRec ? ViewerSession.RecState.On : v.Rec;
            recBtn.IsEnabled = v.State == ViewerState.Connected || rec != ViewerSession.RecState.Off; recBtn.IsChecked = rec != ViewerSession.RecState.Off;
            recBtn.Content = rec == ViewerSession.RecState.Asking ? "Record (waiting for their PC...)" : rec == ViewerSession.RecState.On ? "\u25cf Recording " + SessionRecorder.Elapsed(snapRec ? TimeSpan.FromSeconds(83) : DateTime.Now - v.RecStarted) : "Record";

            stats.ToolTip = string.IsNullOrEmpty(v.PathNote) ? null : v.PathNote;
            monitors.Children.Clear();
            if (v.Monitors.Length > 1)
                for (int i = 0; i < v.Monitors.Length; i++)
                {
                    var n = i; var b = new ToggleButton { Content = "Screen " + (i + 1), Style = Ui.S("Chip"), Margin = new Thickness(0, 0, 6, 4), IsChecked = v.Monitor == i };
                    b.Click += (s, e) => { v.SetMonitor(n); };
                    monitors.Children.Add(b);
                }
            if (v.State == ViewerState.Ended) { Title = "Hallow Assist - session ended"; waitPanel.Visibility = Visibility.Collapsed; banner.Text = v.EndReason; banner.Visibility = Visibility.Visible; }
        }
        void ShowStats()
        {
            var now = DateTime.UtcNow; var secs = Math.Max(0.5, (now - lastStats).TotalSeconds);
            var fps = (v.Frames - lastFrames) / secs; var kbps = (v.FrameBytes - lastBytes) * 8 / secs / 1000;
            lastFrames = v.Frames; lastBytes = v.FrameBytes; lastStats = now;
            if (v.State == ViewerState.Connected) stats.Text = fps.ToString("0") + " fps  ·  " + (kbps >= 1000 ? (kbps / 1000).ToString("0.0") + " Mbit/s" : kbps.ToString("0") + " kbit/s") + "  ·  " + v.RttMs + " ms" + (v.QualityLabel != "" ? "  ·  " + v.QualityLabel : "") + (v.Direct ? "  ·  direct" : "  ·  via relay") + "  ·  encrypted";
        }
        void OpenChat()
        {
            if (chat == null) { chat = new ChatWindow("them") { Owner = this, Topmost = false }; chat.Send = t => v.SendChat(t); }
            if (!chat.IsVisible) chat.Show();
        }
        void PickFile()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = "Send a file to them (they're asked first)" };
            if (d.ShowDialog(this) == true) Note(v.SendFile(d.FileName));
        }
        void Note(string t) { if (!string.IsNullOrEmpty(t)) status.Text = t; }

        // --snap: a made-up picture, never a real screen
        public void SnapRecording() { snapRec = true; Refresh(); }
        public void SnapFill()
        {
            v.State = ViewerState.Connected; v.ControlAllowed = true; v.Safety = "4827"; v.Status = "Connected."; v.QualityLabel = "good";
            v.Monitors = SyntheticSource.Mons;
            var src = new SyntheticSource(); System.Collections.Generic.List<System.Drawing.Rectangle> d; src.Capture(0, 1, out d);
            lock (v.ScreenLock) { v.Screen.Resize(src.FrameW, src.FrameH); Buffer.BlockCopy(src.Frame, 0, v.Screen.Pixels, 0, src.Frame.Length); }
            dirty = new System.Drawing.Rectangle(0, 0, src.FrameW, src.FrameH); Paint();
            stats.Text = "15 fps  ·  2.4 Mbit/s  ·  38 ms  ·  good  ·  direct  ·  encrypted";
            Refresh();
        }
    }
}

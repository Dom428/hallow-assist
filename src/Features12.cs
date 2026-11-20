// Hallow Assist 1.2: online status for Your PCs, the connection history on the PC being helped, drawing on their screen,
// and restarting one of your own PCs with an automatic reconnect afterwards.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace HallowAssist
{
    // ---- online status: asks the relay which of Your PCs are waiting (only unattended codes are ever answered)
    public static class RelayStatus
    {
        // code -> "online" | "busy" | "offline"; null when the relay couldn't be asked (old relay, no network)
        public static Dictionary<string, string> Query(Uri relay, IEnumerable<string> codes, int timeoutMs = 6000)
        {
            var list = codes.Select(Kdf.NormCode).Where(c => c.Length == 9).Distinct().Take(20).ToList();
            if (relay == null || list.Count == 0) return null;
            Dictionary<string, object> answer = null; var got = new ManualResetEvent(false);
            var link = new RelayLink();
            try
            {
                link.Text += s => { try { var m = Msg.FromJson(s); if (Msg.Str(m, "t") == "status") { answer = m; got.Set(); } } catch { } };
                link.Closed += r => got.Set();
                link.Connect(relay, timeoutMs);
                link.SendText(Msg.ToJson(new Dictionary<string, object> { { "t", "status" }, { "codes", list.ToArray() } }));
                got.WaitOne(timeoutMs);
            }
            catch { return null; }
            finally { link.Close("status"); }
            if (answer == null) return null;
            return Parse(answer, list);
        }
        public static Dictionary<string, string> Parse(Dictionary<string, object> m, IEnumerable<string> asked)
        {
            Func<string, HashSet<string>> set = k => { object o; var h = new HashSet<string>(); if (m.TryGetValue(k, out o) && o is System.Collections.ArrayList) foreach (var x in (System.Collections.ArrayList)o) h.Add(Kdf.NormCode(Convert.ToString(x))); return h; };
            var on = set("online"); var busy = set("busy");
            return asked.ToDictionary(c => c, c => on.Contains(c) ? "online" : busy.Contains(c) ? "busy" : "offline");
        }
    }

    // ---- connection history: who connected to this PC, when, for how long, and whether they used the mouse and keyboard
    [DataContract]
    public class HistoryEntry
    {
        [DataMember] public string Start, End, Helper, Client;
        [DataMember] public bool Unattended, ControlAllowed, InputUsed;
        [DataMember] public int FilesReceived;
    }
    public static class History
    {
        public const int Max = 200;
        public static string DefaultPath { get { return System.IO.Path.Combine(AssistSettings.Dir, "history.json"); } }
        static readonly object gate = new object();
        public static List<HistoryEntry> Load(string path = null)
        {
            path = path ?? DefaultPath;
            lock (gate)
                try { if (File.Exists(path)) using (var f = File.OpenRead(path)) return ((List<HistoryEntry>)new DataContractJsonSerializer(typeof(List<HistoryEntry>)).ReadObject(f) ?? new List<HistoryEntry>()).Where(e => e != null).ToList(); }
                catch { }
            return new List<HistoryEntry>();
        }
        // newest first, at most Max
        public static void Add(HistoryEntry e, string path = null)
        {
            if (AssistSettings.Frozen) return;
            path = path ?? DefaultPath;
            lock (gate)
            {
                var all = Load(path); all.Insert(0, e); if (all.Count > Max) all.RemoveRange(Max, all.Count - Max);
                Save(all, path);
            }
        }
        public static void Clear(string path = null) { lock (gate) Save(new List<HistoryEntry>(), path ?? DefaultPath); }
        static void Save(List<HistoryEntry> all, string path)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                using (var f = File.Create(tmp)) new DataContractJsonSerializer(typeof(List<HistoryEntry>)).WriteObject(f, all);
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            catch { }
        }
        static DateTime T(string iso) { DateTime d; return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d : DateTime.MinValue; }
        public static string Describe(HistoryEntry e, DateTime now)
        {
            var s = T(e.Start); var end = T(e.End);
            var day = s.Date == now.Date ? "Today" : s.Date == now.Date.AddDays(-1) ? "Yesterday" : s.ToString("d MMM yyyy");
            var mins = end > s ? (int)Math.Round((end - s).TotalMinutes) : 0;
            var len = end <= s ? "still connected" : mins < 1 ? "under a minute" : mins < 60 ? mins + " min" : (mins / 60) + " h " + (mins % 60) + " min";
            return day + " " + s.ToString("HH:mm") + "  -  " + (e.Helper ?? "Someone") + (e.Client == "browser" ? " (browser)" : "") + "  -  " + len +
                (e.InputUsed ? "  -  used your mouse and keyboard" : e.ControlAllowed ? "  -  could use your mouse and keyboard" : "  -  view only") +
                (e.FilesReceived > 0 ? "  -  " + e.FilesReceived + (e.FilesReceived == 1 ? " file" : " files") + " received" : "") + (e.Unattended ? "  -  unattended" : "");
        }
    }

    public class HistoryWindow : Window
    {
        readonly StackPanel list;
        public HistoryWindow(List<HistoryEntry> entries, Action clear)
        {
            Ui.Dress(this, "Hallow Assist - connection history", 620);
            ResizeMode = ResizeMode.CanMinimize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.T("Who connected to this PC", 17, true, null, 4));
            sp.Children.Add(Ui.Muted("Every session on this PC, newest first (the last " + History.Max + "). Kept only on this PC.", 12.5, 12));
            list = new StackPanel();
            var scroll = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12) };
            sp.Children.Add(scroll);
            Fill(entries);
            var close = Ui.Btn("Close", () => Close()); close.IsCancel = true;
            var clr = Ui.Btn("Clear history", () =>
            {
                if (MessageBox.Show(this, "Clear the connection history on this PC?", "Hallow Assist", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                clear(); Fill(new List<HistoryEntry>());
            });
            var row = Ui.Row(clr, close); row.HorizontalAlignment = HorizontalAlignment.Right; sp.Children.Add(row);
            Content = sp;
        }
        void Fill(List<HistoryEntry> entries)
        {
            list.Children.Clear();
            if (entries.Count == 0) { list.Children.Add(Ui.Muted("Nobody has connected to this PC yet.", 12.5)); return; }
            var now = DateTime.Now;
            foreach (var e in entries)
            {
                var t = Ui.T(History.Describe(e, now), 12.5, false, e.InputUsed ? null : Ui.R("Muted"), 6); t.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(t);
            }
        }
    }

    // ---- drawing on their screen: the helper's strokes, in a see-through, click-through layer over one monitor
    public static class Draw
    {
        public const int MaxPoints = 400;
        // {"t":"draw","id":n,"mon":i,"pts":[x0,y0,x1,y1,...]} with 0..65535 coordinates; null if it isn't a usable stroke
        public static int[] Points(Dictionary<string, object> m)
        {
            object o; if (m == null || !m.TryGetValue("pts", out o) || !(o is System.Collections.ArrayList)) return null;
            var a = (System.Collections.ArrayList)o; if (a.Count < 2 || a.Count % 2 != 0 || a.Count > MaxPoints * 2) return null;
            var p = new int[a.Count];
            for (int i = 0; i < a.Count; i++) { long v; try { v = Convert.ToInt64(a[i]); } catch { return null; } p[i] = (int)Math.Max(0, Math.Min(65535, v)); }
            return p;
        }
    }

    public class DrawOverlay : Window
    {
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
        readonly Canvas canvas = new Canvas();
        readonly Dictionary<long, System.Windows.Shapes.Path> strokes = new Dictionary<long, System.Windows.Shapes.Path>();
        readonly Dictionary<long, PolyLineSegment> segs = new Dictionary<long, PolyLineSegment>();
        readonly ScaleTransform scale = new ScaleTransform(1, 1);   // 0..65535 -> this window's size; the pen keeps its width
        readonly Dictionary<long, DateTime> touched = new Dictionary<long, DateTime>();
        readonly DispatcherTimer fade;
        MonitorInfo mon;
        public DrawOverlay()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize; Focusable = false; Title = "Hallow Assist drawing";
            Content = canvas;
            SizeChanged += (s, e) => { scale.ScaleX = ActualWidth / 65535.0; scale.ScaleY = ActualHeight / 65535.0; };
            SourceInitialized += (s, e) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
                Place();
            };
            fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            fade.Tick += (s, e) => Fade();
        }
        void Place()
        {
            if (mon == null) return;
            var h = new WindowInteropHelper(this).Handle; if (h == IntPtr.Zero) return;
            SetWindowPos(h, new IntPtr(-1) /* HWND_TOPMOST */, mon.X, mon.Y, mon.W, mon.H, 0x0010 /* SWP_NOACTIVATE */ | 0x0040 /* SWP_SHOWWINDOW */);
        }
        // a stroke's points (0..65535 across the monitor) - added to the stroke with that id, or a new stroke
        public void Add(MonitorInfo m, long id, int[] pts)
        {
            if (m == null || pts == null) return;
            if (mon == null || mon.X != m.X || mon.Y != m.Y || mon.W != m.W || mon.H != m.H) { mon = m; Clear(); if (IsVisible) Place(); }
            if (!IsVisible) { Show(); Place(); }
            System.Windows.Shapes.Path line; PolyLineSegment seg;
            if (!strokes.TryGetValue(id, out line))
            {
                if (strokes.Count >= 50) Clear();
                seg = new PolyLineSegment();
                var fig = new PathFigure { StartPoint = new Point(pts[0], pts[1]), IsFilled = false }; fig.Segments.Add(seg);
                var geo = new PathGeometry { Transform = scale }; geo.Figures.Add(fig);
                line = new System.Windows.Shapes.Path { Data = geo, Stroke = Ui.Accent, StrokeThickness = 5, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
                line.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 0.7, Color = Colors.Black };
                strokes[id] = line; segs[id] = seg; canvas.Children.Add(line);
            }
            else seg = segs[id];
            for (int i = 0; i + 1 < pts.Length; i += 2) seg.Points.Add(new Point(pts[i], pts[i + 1]));
            line.Opacity = 1; touched[id] = DateTime.UtcNow;
            if (!fade.IsEnabled) fade.Start();
        }
        // strokes stay 4 s after their last point, then fade out over 1 s
        void Fade()
        {
            var now = DateTime.UtcNow;
            foreach (var id in touched.Keys.ToList())
            {
                var age = (now - touched[id]).TotalSeconds;
                if (age > 5) { canvas.Children.Remove(strokes[id]); strokes.Remove(id); segs.Remove(id); touched.Remove(id); }
                else if (age > 4) strokes[id].Opacity = 5 - age;
            }
            if (strokes.Count == 0) { fade.Stop(); Hide(); }
        }
        public void Clear() { canvas.Children.Clear(); strokes.Clear(); segs.Clear(); touched.Clear(); }
    }

    // ---- restart one of your own PCs (unattended access only): Windows restarts in 10 s, Assist comes back with Windows
    public static class Power
    {
        public static string RestartArgs(string helper)
        {
            var who = new string((helper ?? "your helper").Where(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '-' || ch == '.' || ch == '\'').ToArray()).Trim();
            if (who.Length == 0) who = "your helper"; if (who.Length > 40) who = who.Substring(0, 40);
            return "/r /t 10 /c \"Hallow Assist: " + who + " is restarting this PC.\"";
        }
    }
}

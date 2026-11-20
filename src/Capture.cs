// Hallow Assist screen capture on the helped PC: DXGI Desktop Duplication (with its dirty rectangles) and a GDI fallback,
// plus a synthetic test screen for developer tests (no real screen is ever read in test mode). The mouse pointer is drawn in.
// Only runs while a session is connected and the person said Allow.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HallowAssist
{
    public class MonitorInfo { public int X, Y, W, H; public bool Primary; public string Name; public Rectangle Rect { get { return new Rectangle(X, Y, W, H); } } }
    public class SecureDesktopException : Exception { public SecureDesktopException() : base("secure desktop") { } }

    public abstract class ScreenSource : IDisposable
    {
        public abstract MonitorInfo[] Monitors();
        public bool SecureDesktop;   // the last capture hit the secure desktop (UAC prompt, lock screen)
        public string Kind = "";

        // the clean picture (no pointer) of one monitor into clean (sized W x H). false = nothing changed since last time.
        // dirty = changed areas in monitor pixels when known (null = unknown, compare everything).
        protected abstract bool Grab(int mon, MonitorInfo m, Bitmap clean, out List<Rectangle> dirty);
        protected virtual CursorShot Cursor(MonitorInfo m) { return CursorShot.Get(m); }

        Bitmap clean, output; int lastMon = -1; double lastScale; CursorShot lastCursor;
        public byte[] Frame; public int FrameW, FrameH;

        // captures monitor mon scaled by scale into Frame (BGRX, FrameW x FrameH). Returns false when nothing changed.
        // dirty: changed areas in Frame pixels (null = unknown).
        public bool Capture(int mon, double scale, out List<Rectangle> dirty)
        {
            dirty = null;
            var mons = Monitors(); if (mon < 0 || mon >= mons.Length) mon = 0;
            var m = mons[mon];
            bool reset = mon != lastMon || scale != lastScale || clean == null || clean.Width != m.W || clean.Height != m.H;
            if (reset)
            {
                if (clean != null) clean.Dispose(); if (output != null) output.Dispose();
                clean = new Bitmap(m.W, m.H, PixelFormat.Format32bppRgb);
                int sw = Math.Max(16, (int)Math.Round(m.W * scale)), sh = Math.Max(16, (int)Math.Round(m.H * scale));
                output = new Bitmap(sw, sh, PixelFormat.Format32bppRgb);
                FrameW = sw; FrameH = sh; Frame = new byte[sw * sh * 4];
                lastMon = mon; lastScale = scale; lastCursor = null; OnReset();
            }
            List<Rectangle> d;
            bool changed;
            try { changed = Grab(mon, m, clean, out d); SecureDesktop = false; }
            catch (SecureDesktopException) { SecureDesktop = true; return false; }
            if (reset) { changed = true; d = null; }
            var cur = Cursor(m);
            bool curChanged = !CursorShot.Same(cur, lastCursor);
            if (!changed && !curChanged) return false;
            double sx = (double)FrameW / m.W, sy = (double)FrameH / m.H;
            using (var g = Graphics.FromImage(output))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                if (FrameW == m.W && FrameH == m.H) g.DrawImageUnscaled(clean, 0, 0);
                else { g.InterpolationMode = InterpolationMode.Bilinear; g.PixelOffsetMode = PixelOffsetMode.HighSpeed; g.DrawImage(clean, new Rectangle(0, 0, FrameW, FrameH), 0, 0, m.W, m.H, GraphicsUnit.Pixel); }
                g.CompositingMode = CompositingMode.SourceOver;
                if (cur != null) cur.Draw(g, sx, sy);
            }
            if (d != null)
            {
                var list = new List<Rectangle>();
                foreach (var r in d) list.Add(Scale(r, sx, sy));
                if (cur != null) list.Add(Scale(cur.Bounds, sx, sy));
                if (lastCursor != null) list.Add(Scale(lastCursor.Bounds, sx, sy));
                dirty = list;
            }
            lastCursor = cur;
            var bd = output.LockBits(new Rectangle(0, 0, FrameW, FrameH), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { for (int y = 0; y < FrameH; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, Frame, y * FrameW * 4, FrameW * 4); }
            finally { output.UnlockBits(bd); }
            return true;
        }
        static Rectangle Scale(Rectangle r, double sx, double sy) { var o = Rectangle.FromLTRB((int)Math.Floor(r.Left * sx) - 2, (int)Math.Floor(r.Top * sy) - 2, (int)Math.Ceiling(r.Right * sx) + 2, (int)Math.Ceiling(r.Bottom * sy) + 2); return o; }
        protected virtual void OnReset() { }
        public virtual void Dispose() { if (clean != null) clean.Dispose(); if (output != null) output.Dispose(); }

        // ---- the real monitors, in physical pixels (the app is per-monitor DPI aware)
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        delegate bool MonitorEnumProc(IntPtr h, IntPtr dc, ref RECT r, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        public static MonitorInfo[] RealMonitors()
        {
            var list = new List<MonitorInfo>();
            MonitorEnumProc cb = (IntPtr h, IntPtr dc, ref RECT r, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(h, ref mi)) list.Add(new MonitorInfo { X = mi.rcMonitor.L, Y = mi.rcMonitor.T, W = mi.rcMonitor.R - mi.rcMonitor.L, H = mi.rcMonitor.B - mi.rcMonitor.T, Primary = (mi.dwFlags & 1) != 0, Name = mi.szDevice });
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            list.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            return list.ToArray();
        }
        public static ScreenSource Create()
        {
            if (AssistSettings.TestDir != null) return new SyntheticSource();   // tests never read the real screen
            return new DxgiSource();
        }
    }

    // the mouse pointer, drawn into the picture
    public class CursorShot
    {
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        public IntPtr Handle; public int X, Y, HotX, HotY;   // X, Y: pointer position in monitor pixels
        public Rectangle Bounds { get { return new Rectangle(X - HotX, Y - HotY, 48, 48); } }
        static readonly Dictionary<IntPtr, Point> hot = new Dictionary<IntPtr, Point>();
        public static CursorShot Get(MonitorInfo m)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return null;
            if (!m.Rect.Contains(ci.pt.X, ci.pt.Y)) return null;
            Point h;
            if (!hot.TryGetValue(ci.hCursor, out h))
            {
                ICONINFO ii; if (GetIconInfo(ci.hCursor, out ii)) { h = new Point(ii.xHotspot, ii.yHotspot); if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask); if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor); }
                if (hot.Count > 64) hot.Clear(); hot[ci.hCursor] = h;
            }
            return new CursorShot { Handle = ci.hCursor, X = ci.pt.X - m.X, Y = ci.pt.Y - m.Y, HotX = h.X, HotY = h.Y };
        }
        public static bool Same(CursorShot a, CursorShot b) { if (a == null || b == null) return a == b; return a.Handle == b.Handle && a.X == b.X && a.Y == b.Y; }
        public virtual void Draw(Graphics g, double sx, double sy)
        {
            var dc = g.GetHdc();
            try { DrawIconEx(dc, (int)Math.Round((X - HotX) * sx), (int)Math.Round((Y - HotY) * sy), Handle, (int)Math.Round(32 * sx), (int)Math.Round(32 * sy), 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(dc); }
        }
    }

    // GDI: works everywhere, no change information (the tile encoder compares)
    public class GdiSource : ScreenSource
    {
        public GdiSource() { Kind = "GDI"; }
        public bool GrabInto(MonitorInfo m, Bitmap clean) { List<Rectangle> d; return Grab(0, m, clean, out d); }
        public override MonitorInfo[] Monitors() { return RealMonitors(); }
        protected override bool Grab(int mon, MonitorInfo m, Bitmap clean, out List<Rectangle> dirty)
        {
            dirty = null;
            if (Desktop.SecureDesktopActive()) throw new SecureDesktopException();
            try { using (var g = Graphics.FromImage(clean)) g.CopyFromScreen(m.X, m.Y, 0, 0, new Size(m.W, m.H), CopyPixelOperation.SourceCopy); }
            catch (System.ComponentModel.Win32Exception) { throw new SecureDesktopException(); }
            return true;
        }
    }

    // DXGI Desktop Duplication: the GPU tells us what changed. Falls back to GDI for a monitor it can't duplicate.
    public class DxgiSource : ScreenSource
    {
        readonly GdiSource gdi = new GdiSource();
        public DxgiSource() { Kind = "DXGI"; }
        public override MonitorInfo[] Monitors() { return RealMonitors(); }
        Dup dup; int dupMon = -1; readonly HashSet<string> broken = new HashSet<string>();
        public string LastError;
        protected override void OnReset() { if (dup != null) { dup.Dispose(); dup = null; } dupMon = -1; }
        protected override bool Grab(int mon, MonitorInfo m, Bitmap clean, out List<Rectangle> dirty)
        {
            dirty = null;
            if (broken.Contains(m.Name)) { Kind = "GDI"; return GdiGrab(m, clean); }
            try
            {
                if (dup == null || dupMon != mon) { if (dup != null) dup.Dispose(); dup = Dup.Open(m); dupMon = mon; }
                Kind = "DXGI";
                return dup.Grab(clean, out dirty);
            }
            catch (SecureDesktopException) { if (dup != null) { dup.Dispose(); dup = null; } throw; }
            catch (Exception e)
            {
                LastError = e.Message;
                if (dup != null) { dup.Dispose(); dup = null; }
                if (e is Dup.LostException) return GdiGrab(m, clean);   // try DXGI again next time
                broken.Add(m.Name); Kind = "GDI";
                return GdiGrab(m, clean);
            }
        }
        bool GdiGrab(MonitorInfo m, Bitmap clean) { return gdi.GrabInto(m, clean); }
        public override void Dispose() { if (dup != null) dup.Dispose(); base.Dispose(); }

        // one duplicated output (raw COM through the vtables; no extra libraries)
        sealed class Dup : IDisposable
        {
            public class LostException : Exception { public LostException() : base("access lost") { } }
            [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr f);
            [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels, uint sdk, out IntPtr dev, out int level, out IntPtr ctx);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumFn(IntPtr self, uint i, out IntPtr o);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OutDescFn(IntPtr self, out OUTDESC d);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DuplicateFn(IntPtr self, IntPtr dev, out IntPtr dup);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AcquireFn(IntPtr self, uint ms, IntPtr info, out IntPtr res);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DirtyFn(IntPtr self, uint size, IntPtr buf, out uint need);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrameFn(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexFn(IntPtr self, ref TEXDESC d, IntPtr init, out IntPtr tex);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void TexDescFn(IntPtr self, out TEXDESC d);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyFn(IntPtr self, IntPtr dst, IntPtr src);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MAPPED m);
            [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr self, IntPtr res, uint sub);
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct OUTDESC { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; public int L, T, R, B; public int Attached; public int Rotation; public IntPtr Monitor; }
            [StructLayout(LayoutKind.Sequential)] struct TEXDESC { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }
            [StructLayout(LayoutKind.Sequential)] struct MAPPED { public IntPtr Data; public uint RowPitch, DepthPitch; }
            const int WAIT_TIMEOUT = unchecked((int)0x887A0027), ACCESS_LOST = unchecked((int)0x887A0026), ACCESS_DENIED = unchecked((int)0x80070005);
            static T Fn<T>(IntPtr o, int slot) where T : class { var vt = Marshal.ReadIntPtr(o); return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vt, slot * IntPtr.Size), typeof(T)); }
            static void Rel(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; } }

            IntPtr dev, ctx, dupl, staging; int w, h; byte[] frameInfo = new byte[64]; bool haveFrame;

            public static Dup Open(MonitorInfo m)
            {
                var d = new Dup(); IntPtr factory = IntPtr.Zero;
                try
                {
                    var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
                    Check(CreateDXGIFactory1(ref iid, out factory));
                    for (uint a = 0; ; a++)
                    {
                        IntPtr adapter; if (Fn<EnumFn>(factory, 12)(factory, a, out adapter) != 0) break;
                        try
                        {
                            for (uint o = 0; ; o++)
                            {
                                IntPtr output; if (Fn<EnumFn>(adapter, 7)(adapter, o, out output) != 0) break;
                                try
                                {
                                    OUTDESC od; Fn<OutDescFn>(output, 7)(output, out od);
                                    if (od.L != m.X || od.T != m.Y || od.R - od.L != m.W || od.B - od.T != m.H) continue;
                                    if (od.Rotation > 1) throw new NotSupportedException("rotated screen");
                                    int level; Check(D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d.dev, out level, out d.ctx));
                                    var iid1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc"); IntPtr out1;
                                    Check(Marshal.QueryInterface(output, ref iid1, out out1));
                                    try
                                    {
                                        int hr = Fn<DuplicateFn>(out1, 22)(out1, d.dev, out d.dupl);
                                        if (hr == ACCESS_DENIED) throw new SecureDesktopException();
                                        Check(hr);
                                    }
                                    finally { Marshal.Release(out1); }
                                    d.w = m.W; d.h = m.H;
                                    return d;
                                }
                                finally { Marshal.Release(output); }
                            }
                        }
                        finally { Marshal.Release(adapter); }
                    }
                    throw new InvalidOperationException("no DXGI output for this monitor");
                }
                catch { d.Dispose(); throw; }
                finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
            }
            static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

            public bool Grab(Bitmap clean, out List<Rectangle> dirty)
            {
                dirty = null;
                IntPtr res; var pin = GCHandle.Alloc(frameInfo, GCHandleType.Pinned);
                int hr;
                try { hr = Fn<AcquireFn>(dupl, 8)(dupl, haveFrame ? 0u : 100u, pin.AddrOfPinnedObject(), out res); }
                finally { pin.Free(); }
                if (hr == WAIT_TIMEOUT) return false;
                if (hr == ACCESS_LOST) throw new LostException();
                if (hr == ACCESS_DENIED) throw new SecureDesktopException();
                Check(hr);
                IntPtr tex = IntPtr.Zero;
                try
                {
                    long lastPresent = BitConverter.ToInt64(frameInfo, 0);
                    if (lastPresent == 0 && haveFrame) return false;   // only the pointer moved; we draw it ourselves
                    var iid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    Check(Marshal.QueryInterface(res, ref iid, out tex));
                    if (staging == IntPtr.Zero)
                    {
                        TEXDESC td; Fn<TexDescFn>(tex, 10)(tex, out td);
                        td.MipLevels = 1; td.ArraySize = 1; td.SampleCount = 1; td.SampleQuality = 0; td.Usage = 3; td.BindFlags = 0; td.CpuAccess = 0x20000; td.Misc = 0;
                        Check(Fn<CreateTexFn>(dev, 5)(dev, ref td, IntPtr.Zero, out staging));
                        w = (int)td.Width; h = (int)td.Height;
                    }
                    if (haveFrame) dirty = DirtyRects();
                    Fn<CopyFn>(ctx, 47)(ctx, staging, tex);
                    MAPPED mp; Check(Fn<MapFn>(ctx, 14)(ctx, staging, 0, 1, 0, out mp));
                    try
                    {
                        int cw = Math.Min(w, clean.Width), ch = Math.Min(h, clean.Height);
                        var bd = clean.LockBits(new Rectangle(0, 0, cw, ch), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                        try { var row = new byte[cw * 4]; for (int y = 0; y < ch; y++) { Marshal.Copy(mp.Data + y * (int)mp.RowPitch, row, 0, row.Length); Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, row.Length); } }
                        finally { clean.UnlockBits(bd); }
                    }
                    finally { Fn<UnmapFn>(ctx, 15)(ctx, staging, 0); }
                    haveFrame = true;
                    return true;
                }
                finally
                {
                    if (tex != IntPtr.Zero) Marshal.Release(tex);
                    Marshal.Release(res);
                    Fn<ReleaseFrameFn>(dupl, 14)(dupl);
                }
            }
            List<Rectangle> DirtyRects()
            {
                uint need; var f = Fn<DirtyFn>(dupl, 9);
                f(dupl, 0, IntPtr.Zero, out need);
                if (need == 0) return new List<Rectangle>();
                var buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    if (f(dupl, need, buf, out need) < 0) return null;
                    var list = new List<Rectangle>();
                    for (int i = 0; i < need / 16; i++) { int l = Marshal.ReadInt32(buf, i * 16), t = Marshal.ReadInt32(buf, i * 16 + 4), r = Marshal.ReadInt32(buf, i * 16 + 8), b = Marshal.ReadInt32(buf, i * 16 + 12); list.Add(Rectangle.FromLTRB(l, t, r, b)); }
                    // move rects (scrolling) change areas too; dirty rects alone would miss them, so be safe: compare everything
                    if (BitConverter.ToInt32(frameInfo, 40) > need) return null;
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            public void Dispose() { Rel(ref staging); Rel(ref dupl); Rel(ref ctx); Rel(ref dev); }
        }
    }

    // developer tests: two made-up monitors (one at negative coordinates) with a picture that changes a little every frame
    public class SyntheticSource : ScreenSource
    {
        public int Ticks;
        public static readonly MonitorInfo[] Mons = {
            new MonitorInfo { X = 0, Y = 0, W = 1280, H = 720, Primary = true, Name = "TEST1" },
            new MonitorInfo { X = -1024, Y = -200, W = 1024, H = 768, Primary = false, Name = "TEST2" } };
        public SyntheticSource() { Kind = "test"; }
        public override MonitorInfo[] Monitors() { return Mons; }
        protected override CursorShot Cursor(MonitorInfo m) { return null; }
        protected override bool Grab(int mon, MonitorInfo m, Bitmap clean, out List<Rectangle> dirty)
        {
            dirty = null; Ticks++;
            using (var g = Graphics.FromImage(clean))
            {
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, m.W, m.H), mon == 0 ? Color.FromArgb(40, 30, 60) : Color.FromArgb(20, 60, 50), Color.FromArgb(10, 10, 16), 45f)) g.FillRectangle(bg, 0, 0, m.W, m.H);
                using (var f = new Font("Segoe UI", 28)) g.DrawString("Hallow Assist test screen " + (mon + 1), f, Brushes.White, 40, 40);
                using (var f = new Font("Segoe UI", 14)) g.DrawString("This is a made-up picture for developer tests, not a real screen.", f, Brushes.Silver, 44, 100);
                int x = (Ticks * 24) % (m.W - 120);
                using (var b = new SolidBrush(Color.FromArgb(240, 120, 32))) g.FillRectangle(b, x, m.H - 200, 120, 80);
            }
            return true;
        }
    }
}

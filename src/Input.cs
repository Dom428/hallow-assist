// Hallow Assist input on the helped PC: the helper's mouse and keys, only while the person allowed control.
// Mouse positions arrive as 0..65535 on the monitor being shown and are mapped to that monitor's pixels (monitors can sit at
// negative coordinates). Keys arrive as scan codes. In developer tests a recorder stands in for SendInput.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HallowAssist
{
    public interface IInputSink
    {
        void Move(int px, int py);                 // virtual-screen pixels
        void Button(int button, bool down);        // 0 left, 1 right, 2 middle
        void Wheel(int dy, int dx);
        void Key(int scan, bool down);             // scan code, 0xE0xx = extended
        void Text(string s);
    }

    public static class InputMap
    {
        // 0..65535 on monitor m -> a pixel on that monitor
        public static void ToPixel(MonitorInfo m, int nx, int ny, out int px, out int py)
        {
            nx = Math.Max(0, Math.Min(65535, nx)); ny = Math.Max(0, Math.Min(65535, ny));
            px = m.X + (int)Math.Round(nx * (m.W - 1) / 65535.0);
            py = m.Y + (int)Math.Round(ny * (m.H - 1) / 65535.0);
        }
        // a pixel -> SendInput's absolute 0..65535 over the whole virtual desktop
        public static void ToAbsolute(int px, int py, int vx, int vy, int vw, int vh, out int ax, out int ay)
        {
            ax = (int)Math.Round((px - vx) * 65535.0 / Math.Max(1, vw - 1));
            ay = (int)Math.Round((py - vy) * 65535.0 / Math.Max(1, vh - 1));
        }
        // the virtual desktop that contains these monitors
        public static void Bounds(MonitorInfo[] mons, out int x, out int y, out int w, out int h)
        {
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
            foreach (var m in mons) { l = Math.Min(l, m.X); t = Math.Min(t, m.Y); r = Math.Max(r, m.X + m.W); b = Math.Max(b, m.Y + m.H); }
            x = l; y = t; w = r - l; h = b - t;
        }
    }

    public class SendInputSink : IInputSink
    {
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public int mouseData; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        const uint MOVE = 1, LDOWN = 2, LUP = 4, RDOWN = 8, RUP = 0x10, MDOWN = 0x20, MUP = 0x40, WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
        static void Send(params INPUT[] i) { SendInput((uint)i.Length, i, Marshal.SizeOf(typeof(INPUT))); }
        static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) { return new INPUT { type = 0, u = new UNION { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } }; }
        static INPUT Kb(ushort scan, uint flags) { return new INPUT { type = 1, u = new UNION { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } }; }
        public void Move(int px, int py)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79), ax, ay;
            InputMap.ToAbsolute(px, py, vx, vy, vw, vh, out ax, out ay);
            Send(Mouse(MOVE | ABSOLUTE | VIRTUALDESK, ax, ay));
        }
        public void Button(int b, bool down) { Send(Mouse(b == 1 ? (down ? RDOWN : RUP) : b == 2 ? (down ? MDOWN : MUP) : (down ? LDOWN : LUP))); }
        public void Wheel(int dy, int dx) { if (dy != 0) Send(Mouse(WHEEL, 0, 0, dy)); if (dx != 0) Send(Mouse(HWHEEL, 0, 0, dx)); }
        public void Key(int scan, bool down) { Send(Kb((ushort)(scan & 0xFF), 0x8u | ((scan & 0xFF00) == 0xE000 ? 1u : 0u) | (down ? 0u : 2u))); }
        public void Text(string s)
        {
            var list = new List<INPUT>();
            foreach (var c in s) { if (c == '\r') continue; if (c == '\n') { list.Add(Kb(0x1C, 8)); list.Add(Kb(0x1C, 10)); continue; } list.Add(Kb(c, 4)); list.Add(Kb(c, 6)); }
            if (list.Count > 0) Send(list.ToArray());
        }
    }

    // developer tests: writes what would have been typed/clicked to input-log.jsonl in the test folder
    public class RecordingSink : IInputSink
    {
        readonly string file;
        public RecordingSink(string dir) { file = Path.Combine(dir, "input-log.jsonl"); }
        void Log(string json) { lock (this) File.AppendAllText(file, json + "\n", new UTF8Encoding(false)); }
        public void Move(int px, int py) { Log("{\"t\":\"move\",\"x\":" + px + ",\"y\":" + py + "}"); }
        public void Button(int b, bool down) { Log("{\"t\":\"button\",\"b\":" + b + ",\"down\":" + (down ? "true" : "false") + "}"); }
        public void Wheel(int dy, int dx) { Log("{\"t\":\"wheel\",\"dy\":" + dy + ",\"dx\":" + dx + "}"); }
        public void Key(int scan, bool down) { Log("{\"t\":\"key\",\"scan\":" + scan + ",\"down\":" + (down ? "true" : "false") + "}"); }
        public void Text(string s) { Log("{\"t\":\"text\",\"text\":" + Msg.ToJson(s) + "}"); }
    }
}

// Hallow Assist 1.4: session sound and Wake-on-LAN.
//
// Sound (optional, off on both sides until turned on): the PC being helped records what it plays (WASAPI loopback - its
// speakers' mix, never the microphone), mixes it to mono, brings it down to 16 kHz and packs it with IMA ADPCM (4 bits a
// sample, 64 kbit/s; each 40 ms packet stands alone, so a lost one is just a short gap). Packets go inside the same sealed
// channel as everything else. The helper's app plays them with waveOut. It only flows while the person being helped has
// "Share sound" on (the red bar says "Sound shared") AND the helper has Sound on.
//
// Wake-on-LAN: one of Your PCs tells its owner's app its network adapter's MAC address (sealed, unattended sessions only);
// Wake sends the standard magic packet (6 x FF, then the MAC 16 times) as a UDP broadcast on ports 7 and 9.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;

namespace HallowAssist
{
    // ---------------- the audio message: 0x05 rate(2) predictor(2, signed) index(1) count(2), then count 4-bit samples
    public class AudioMsg
    {
        public const int Rate = 16000, ChunkSamples = 640;   // 40 ms
        public int SampleRate = Rate, Predictor, Index, Count; public byte[] Data;
        public byte[] Encode()
        {
            var b = new byte[8 + Data.Length];
            b[0] = Msg.Audio; b[1] = (byte)(SampleRate >> 8); b[2] = (byte)SampleRate; b[3] = (byte)(Predictor >> 8); b[4] = (byte)Predictor; b[5] = (byte)Index;
            b[6] = (byte)(Count >> 8); b[7] = (byte)Count; Buffer.BlockCopy(Data, 0, b, 8, Data.Length);
            return b;
        }
        public static AudioMsg Parse(byte[] b)
        {
            if (b == null || b.Length < 8 || b[0] != Msg.Audio) throw new System.IO.InvalidDataException("not audio");
            var m = new AudioMsg { SampleRate = Msg.RU16(b, 1), Predictor = Msg.RI16(b, 3), Index = b[5], Count = Msg.RU16(b, 6) };
            if (m.SampleRate < 8000 || m.SampleRate > 48000 || m.Index > 88 || m.Count == 0 || m.Count > 4800 || 8 + (m.Count + 1) / 2 > b.Length) throw new System.IO.InvalidDataException("bad audio packet");
            m.Data = Kdf.Slice(b, 8, (m.Count + 1) / 2);
            return m;
        }
        // state = the encoder's predictor and step index, carried from one packet to the next of the same stream
        public static AudioMsg FromPcm(short[] pcm, int n, int[] state)
        {
            int pred = state[0], idx = state[1]; var data = Adpcm.Encode(pcm, n, ref state[0], ref state[1]);
            return new AudioMsg { Predictor = pred, Index = idx, Count = n, Data = data };
        }
        public short[] ToPcm() { return Adpcm.Decode(Data, Count, Predictor, Index); }
    }

    // IMA ADPCM: each packet starts from the predictor and step index it carries
    public static class Adpcm
    {
        static readonly int[] Steps = { 7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
        static readonly int[] IndexMove = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        // pred/idx: where the stream is now (updated, so the next packet starts where this one ended)
        public static byte[] Encode(short[] pcm, int n, ref int sPred, ref int sIdx)
        {
            int pred = sPred, idx = Math.Max(0, Math.Min(88, sIdx)); var o = new byte[(n + 1) / 2];
            for (int i = 0; i < n; i++)
            {
                int step = Steps[idx], diff = pcm[i] - pred, code = 0;
                if (diff < 0) { code = 8; diff = -diff; }
                int delta = step >> 3;
                if (diff >= step) { code |= 4; diff -= step; delta += step; }
                if (diff >= step >> 1) { code |= 2; diff -= step >> 1; delta += step >> 1; }
                if (diff >= step >> 2) { code |= 1; delta += step >> 2; }
                pred += (code & 8) != 0 ? -delta : delta; pred = Math.Max(-32768, Math.Min(32767, pred));
                idx = Math.Max(0, Math.Min(88, idx + IndexMove[code]));
                if ((i & 1) == 0) o[i >> 1] = (byte)code; else o[i >> 1] |= (byte)(code << 4);
            }
            sPred = pred; sIdx = idx;
            return o;
        }
        public static short[] Decode(byte[] data, int n, int pred, int idx)
        {
            var o = new short[n]; idx = Math.Max(0, Math.Min(88, idx));
            for (int i = 0; i < n; i++)
            {
                int code = (i & 1) == 0 ? data[i >> 1] & 15 : data[i >> 1] >> 4;
                int step = Steps[idx], delta = step >> 3;
                if ((code & 4) != 0) delta += step; if ((code & 2) != 0) delta += step >> 1; if ((code & 1) != 0) delta += step >> 2;
                pred += (code & 8) != 0 ? -delta : delta; pred = Math.Max(-32768, Math.Min(32767, pred));
                idx = Math.Max(0, Math.Min(88, idx + IndexMove[code]));
                o[i] = (short)pred;
            }
            return o;
        }
    }

    // any rate, any channel count -> mono 16 kHz in 40 ms chunks (a box filter: averages the input samples of each output one)
    public class Downsampler
    {
        readonly double ratio; double boundary; long n; double acc; int cnt;
        readonly short[] chunk = new short[AudioMsg.ChunkSamples]; int fill;
        readonly Action<short[]> output;
        public Downsampler(int inRate, Action<short[]> output) { ratio = Math.Max(1.0, inRate / (double)AudioMsg.Rate); boundary = ratio; this.output = output; }
        public void Add(float mono)
        {
            acc += mono; cnt++; n++;
            if (n < boundary) return;
            boundary += ratio;
            var v = acc / cnt; acc = 0; cnt = 0;
            chunk[fill++] = (short)Math.Max(-32768, Math.Min(32767, Math.Round(v * 32767)));
            if (fill == chunk.Length) { fill = 0; output((short[])chunk.Clone()); }
        }
    }

    public interface IAudioSource : IDisposable
    {
        void Start(Action<short[]> chunk16k);   // mono 16 kHz, ChunkSamples at a time, on a background thread
        void Stop();
        string Error { get; }
    }
    public interface IAudioSink
    {
        void Play(short[] pcm16k);
        void Stop();
    }

    // the real thing: what this PC's default speakers play (WASAPI shared-mode loopback)
    public sealed class LoopbackSource : IAudioSource
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }
        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }
        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }
        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            void GetBufferSize(out uint frames);
            void GetStreamLatency(out long latency);
            void GetCurrentPadding(out uint padding);
            void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            void GetMixFormat(out IntPtr format);
            void GetDevicePeriod(out long def, out long min);
            void Start();
            void Stop();
            void Reset();
            void SetEventHandle(IntPtr h);
            void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }
        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devPos, out ulong qpcPos);
            void ReleaseBuffer(uint frames);
            void GetNextPacketSize(out uint frames);
        }
        static readonly Guid FloatSub = new Guid("00000003-0000-0010-8000-00aa00389b71");
        volatile bool running; Thread thread;
        public string Error { get; private set; }
        public string Format;   // e.g. "48000 Hz, 2 ch, float32" (tests)
        public void Start(Action<short[]> chunk)
        {
            if (running) return; running = true;
            thread = new Thread(() => Run(chunk)) { IsBackground = true, Name = "sound capture", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        void Run(Action<short[]> chunk)
        {
            IAudioClient client = null; IntPtr fmt = IntPtr.Zero;
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice dev; en.GetDefaultAudioEndpoint(0 /* eRender */, 0 /* eConsole */, out dev);
                var iid = typeof(IAudioClient).GUID; object o; dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o);
                client = (IAudioClient)o;
                client.GetMixFormat(out fmt);
                int tag = (ushort)Marshal.ReadInt16(fmt, 0), ch = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4), align = Marshal.ReadInt16(fmt, 12), bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3;
                if (tag == 0xFFFE) { var g = new byte[16]; Marshal.Copy(fmt + 24, g, 0, 16); isFloat = new Guid(g) == FloatSub; }
                Format = rate + " Hz, " + ch + " ch, " + (isFloat ? "float" : "pcm") + bits;
                if (ch < 1 || rate < 8000 || (isFloat && bits != 32) || (!isFloat && bits != 16 && bits != 24 && bits != 32)) throw new NotSupportedException("sound format " + Format);
                client.Initialize(0 /* shared */, 0x00020000 /* LOOPBACK */, 2000000 /* 200 ms */, 0, fmt, IntPtr.Zero);
                var cid = typeof(IAudioCaptureClient).GUID; object co; client.GetService(ref cid, out co);
                var cap = (IAudioCaptureClient)co;
                var ds = new Downsampler(rate, chunk);
                client.Start();
                var frame = new byte[align];
                while (running)
                {
                    uint next; cap.GetNextPacketSize(out next);
                    if (next == 0) { Thread.Sleep(10); continue; }
                    IntPtr data; uint frames, flags; ulong p1, p2;
                    cap.GetBuffer(out data, out frames, out flags, out p1, out p2);
                    if ((flags & 2) != 0) { for (uint i = 0; i < frames; i++) ds.Add(0); }   // AUDCLNT_BUFFERFLAGS_SILENT
                    else
                    {
                        var buf = new byte[frames * align]; Marshal.Copy(data, buf, 0, buf.Length);
                        int bps = bits / 8;
                        for (int f = 0; f < frames; f++)
                        {
                            double sum = 0; int b0 = f * align;
                            for (int c = 0; c < ch; c++)
                            {
                                int at = b0 + c * bps;
                                if (isFloat) sum += BitConverter.ToSingle(buf, at);
                                else if (bps == 2) sum += BitConverter.ToInt16(buf, at) / 32768.0;
                                else if (bps == 3) sum += ((buf[at] << 8 | buf[at + 1] << 16 | buf[at + 2] << 24) >> 8) / 8388608.0;
                                else sum += BitConverter.ToInt32(buf, at) / 2147483648.0;
                            }
                            ds.Add((float)Math.Max(-1, Math.Min(1, sum / ch)));
                        }
                    }
                    cap.ReleaseBuffer(frames);
                }
                client.Stop();
            }
            catch (Exception e) { Error = e.Message; running = false; }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (client != null) try { Marshal.ReleaseComObject(client); } catch { }
            }
        }
        public void Stop() { running = false; var t = thread; if (t != null && t != Thread.CurrentThread) t.Join(1000); thread = null; }
        public void Dispose() { Stop(); }
    }

    // test mode: a 440 Hz tone instead of the real sound (never the PC's own)
    public sealed class ToneSource : IAudioSource
    {
        volatile bool running; public string Error { get { return null; } }
        public void Start(Action<short[]> chunk)
        {
            if (running) return; running = true;
            new Thread(() =>
            {
                long t = 0; var sw = System.Diagnostics.Stopwatch.StartNew(); long sent = 0;
                while (running)
                {
                    while (running && sent * 40 <= sw.ElapsedMilliseconds)
                    {
                        var c = new short[AudioMsg.ChunkSamples];
                        for (int i = 0; i < c.Length; i++, t++) c[i] = (short)(Math.Sin(2 * Math.PI * 440 * t / AudioMsg.Rate) * 12000);
                        chunk(c); sent++;
                    }
                    Thread.Sleep(10);
                }
            }) { IsBackground = true, Name = "test tone" }.Start();
        }
        public void Stop() { running = false; }
        public void Dispose() { Stop(); }
    }

    // test mode: counts what would be played and estimates its pitch (never makes a sound)
    public sealed class CountingSink : IAudioSink
    {
        public long Samples, Packets; long crossings; short last;
        public void Play(short[] p) { lock (this) { Packets++; foreach (var s in p) { if ((s >= 0) != (last >= 0)) crossings++; last = s; Samples++; } } }
        public void Stop() { }
        public int Hz { get { lock (this) return Samples == 0 ? 0 : (int)Math.Round(crossings / 2.0 * AudioMsg.Rate / Samples); } }
    }

    // the helper's speakers: waveOut, 16 kHz mono 16-bit, a small jitter buffer (starts after 120 ms, drops beyond ~480 ms)
    public sealed class WaveOutSink : IAudioSink
    {
        [StructLayout(LayoutKind.Sequential, Pack = 2)] struct WaveFormat { public ushort Tag, Channels; public uint Rate, AvgBytes; public ushort Align, Bits, Size; }
        [StructLayout(LayoutKind.Sequential)] struct WaveHdr { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, int dev, ref WaveFormat fmt, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);
        const int Buffers = 16, MaxQueued = 12, Prebuffer = 3, WHDR_DONE = 1, WHDR_PREPARED = 2;
        static readonly int HdrSize = Marshal.SizeOf(typeof(WaveHdr)), FlagsAt = (int)Marshal.OffsetOf(typeof(WaveHdr), "Flags");
        IntPtr dev; readonly IntPtr[] hdrs = new IntPtr[Buffers]; readonly bool[] used = new bool[Buffers];
        readonly List<short[]> waiting = new List<short[]>();
        public string Error; public long Played, Dropped;
        bool Open()
        {
            if (dev != IntPtr.Zero) return true;
            var f = new WaveFormat { Tag = 1, Channels = 1, Rate = AudioMsg.Rate, AvgBytes = AudioMsg.Rate * 2, Align = 2, Bits = 16, Size = 0 };
            int r = waveOutOpen(out dev, -1 /* WAVE_MAPPER */, ref f, IntPtr.Zero, IntPtr.Zero, 0);
            if (r != 0) { Error = "waveOutOpen " + r; dev = IntPtr.Zero; return false; }
            for (int i = 0; i < Buffers; i++)
            {
                var data = Marshal.AllocHGlobal(4800 * 2);
                var h = new WaveHdr { Data = data, Length = 0 };
                hdrs[i] = Marshal.AllocHGlobal(HdrSize); Marshal.StructureToPtr(h, hdrs[i], false); used[i] = false;
            }
            return true;
        }
        bool Busy(int i) { return used[i] && (Marshal.ReadInt32(hdrs[i], FlagsAt) & WHDR_DONE) == 0; }
        int InFlight() { int n = 0; for (int i = 0; i < Buffers; i++) if (Busy(i)) n++; return n; }
        void Write(short[] p)
        {
            for (int i = 0; i < Buffers; i++)
            {
                if (Busy(i)) continue;
                if (used[i] && (Marshal.ReadInt32(hdrs[i], FlagsAt) & WHDR_PREPARED) != 0) waveOutUnprepareHeader(dev, hdrs[i], HdrSize);
                var h = (WaveHdr)Marshal.PtrToStructure(hdrs[i], typeof(WaveHdr));
                int n = Math.Min(p.Length, 4800);
                Marshal.Copy(p, 0, h.Data, n); h.Length = (uint)(n * 2); h.Flags = 0; h.Recorded = 0;
                Marshal.StructureToPtr(h, hdrs[i], false);
                used[i] = true;
                if (waveOutPrepareHeader(dev, hdrs[i], HdrSize) != 0 || waveOutWrite(dev, hdrs[i], HdrSize) != 0) { used[i] = false; Dropped++; return; }
                Played++; return;
            }
            Dropped++;
        }
        public void Play(short[] p)
        {
            lock (this)
            {
                if (!Open()) return;
                int busy = InFlight();
                if (busy >= MaxQueued) { Dropped++; return; }        // too far behind: keep the delay short
                if (busy == 0) { waiting.Add(p); if (waiting.Count < Prebuffer) return; foreach (var w in waiting) Write(w); waiting.Clear(); return; }
                Write(p);
            }
        }
        public void Stop()
        {
            lock (this)
            {
                waiting.Clear();
                if (dev == IntPtr.Zero) return;
                waveOutReset(dev);
                for (int i = 0; i < Buffers; i++)
                {
                    if (hdrs[i] == IntPtr.Zero) continue;
                    if ((Marshal.ReadInt32(hdrs[i], FlagsAt) & WHDR_PREPARED) != 0) waveOutUnprepareHeader(dev, hdrs[i], HdrSize);
                    var h = (WaveHdr)Marshal.PtrToStructure(hdrs[i], typeof(WaveHdr)); Marshal.FreeHGlobal(h.Data); Marshal.FreeHGlobal(hdrs[i]); hdrs[i] = IntPtr.Zero; used[i] = false;
                }
                waveOutClose(dev); dev = IntPtr.Zero;
            }
        }
    }

    // ---------------- Wake-on-LAN
    public static class Wol
    {
        public static readonly int[] Ports = { 7, 9 };
        // "AA:BB:CC:DD:EE:FF", "aa-bb-...", "AABBCCDDEEFF" -> 6 bytes; null if it isn't a usable MAC (not all zero, not broadcast/multicast)
        public static byte[] ParseMac(string s)
        {
            if (s == null) return null;
            var hex = new string(s.Where(c => c != ':' && c != '-' && c != ' ' && c != '.').ToArray());
            if (hex.Length != 12 || !hex.All(Uri.IsHexDigit)) return null;
            var b = new byte[6]; for (int i = 0; i < 6; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            if (b.All(x => x == 0) || (b[0] & 1) != 0) return null;
            return b;
        }
        public static string NormMac(string s) { var b = ParseMac(s); return b == null ? null : BitConverter.ToString(b).Replace("-", ""); }
        public static string ShowMac(string s) { var b = ParseMac(s); return b == null ? "" : BitConverter.ToString(b).Replace("-", ":"); }
        public static byte[] Packet(byte[] mac)
        {
            if (mac == null || mac.Length != 6) throw new ArgumentException("mac");
            var p = new byte[102];
            for (int i = 0; i < 6; i++) p[i] = 0xFF;
            for (int r = 0; r < 16; r++) Buffer.BlockCopy(mac, 0, p, 6 + r * 6, 6);
            return p;
        }
        // where the packets go: every home-network adapter's subnet broadcast and 255.255.255.255, ports 7 and 9. In test mode
        // only 127.0.0.1 on HALLOW_ASSIST_WOL_PORT - tests never broadcast onto the real network.
        public static List<IPEndPoint> Targets()
        {
            var o = new List<IPEndPoint>();
            if (AssistSettings.TestDir != null)
            {
                int port; if (int.TryParse(Environment.GetEnvironmentVariable("HALLOW_ASSIST_WOL_PORT") ?? "", out port) && port > 0 && port < 65536) o.Add(new IPEndPoint(IPAddress.Loopback, port));
                return o;
            }
            var addrs = new List<IPAddress>();
            foreach (var a in DirectNet.Adapters()) { if (DirectNet.LooksVirtual(a)) continue; var b = DirectNet.Broadcast(a.Address, a.Mask); if (!addrs.Contains(b)) addrs.Add(b); }
            addrs.Add(IPAddress.Broadcast);
            foreach (var a in addrs) foreach (var p in Ports) o.Add(new IPEndPoint(a, p));
            return o;
        }
        // number of packets that left this PC
        public static int Send(string mac, IEnumerable<IPEndPoint> targets)
        {
            var m = ParseMac(mac); if (m == null) return 0;
            var pkt = Packet(m); int sent = 0;
            using (var u = new UdpClient(AddressFamily.InterNetwork))
            {
                u.EnableBroadcast = true;
                foreach (var t in targets) { try { if (u.Send(pkt, pkt.Length, t) == pkt.Length) sent++; } catch { } }
            }
            return sent;
        }
    }
}

// Hallow Assist messages inside the sealed channel (same layout as proto.js). Big-endian numbers.
//   0x01 JSON     utf-8 JSON control message ({"t":"hello"}, consent, chat, clip, file-offer, ack, ...)
//   0x02 FRAME    mon(1) srcW(2) srcH(2) encW(2) encH(2) seq(4) flags(1) count(2), then count x [x y w h (2 each) len(4) jpeg]
//   0x03 INPUT    kind(1) ...: 1 move nx ny | 2 button b down nx ny | 3 wheel dy dx | 4 key scan down | 5 text utf-8
//   0x04 FILE     id(4) data
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace HallowAssist
{
    public static class Msg
    {
        public const byte VHello = 1, HHello = 2, VConfirm = 3, HConfirm = 4, HReject = 5, Ver = 1;
        public const byte Json = 1, Frame = 2, Input = 3, File = 4;
        public const byte InMove = 1, InButton = 2, InWheel = 3, InKey = 4, InText = 5;
        static readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };

        public static byte[] JsonMsg(Dictionary<string, object> o) { return Kdf.Cat(new[] { Json }, Kdf.Utf8(js.Serialize(o))); }
        public static byte[] JsonMsg(params object[] kv) { var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1]; return JsonMsg(d); }
        public static Dictionary<string, object> ParseJson(byte[] b) { return js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(b, 1, b.Length - 1)); }
        public static string ToJson(object o) { return js.Serialize(o); }
        public static Dictionary<string, object> FromJson(string s) { return js.Deserialize<Dictionary<string, object>>(s); }
        public static string Str(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null; }
        public static long Num(Dictionary<string, object> d, string k) { object o; if (d == null || !d.TryGetValue(k, out o) || o == null) return 0; try { return Convert.ToInt64(o, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0; } }
        public static bool Bool(Dictionary<string, object> d, string k) { object o; return d != null && d.TryGetValue(k, out o) && o is bool && (bool)o; }

        public static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        public static void U32(Stream s, long v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        public static int RU16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        public static short RI16(byte[] b, int o) { return (short)((b[o] << 8) | b[o + 1]); }
        public static uint RU32(byte[] b, int o) { return ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3]; }

        public static byte[] Move(int nx, int ny) { var m = new MemoryStream(); m.WriteByte(Input); m.WriteByte(InMove); U16(m, nx); U16(m, ny); return m.ToArray(); }
        public static byte[] Button(int button, bool down, int nx, int ny) { var m = new MemoryStream(); m.WriteByte(Input); m.WriteByte(InButton); m.WriteByte((byte)button); m.WriteByte((byte)(down ? 1 : 0)); U16(m, nx); U16(m, ny); return m.ToArray(); }
        public static byte[] Wheel(int dy, int dx) { var m = new MemoryStream(); m.WriteByte(Input); m.WriteByte(InWheel); U16(m, (ushort)(short)dy); U16(m, (ushort)(short)dx); return m.ToArray(); }
        public static byte[] Key(int scan, bool down) { var m = new MemoryStream(); m.WriteByte(Input); m.WriteByte(InKey); U16(m, scan); m.WriteByte((byte)(down ? 1 : 0)); return m.ToArray(); }
        public static byte[] Text(string s) { if (s.Length > 2000) s = s.Substring(0, 2000); return Kdf.Cat(new[] { Input, InText }, Kdf.Utf8(s)); }
        public static byte[] FileChunk(uint id, byte[] data, int off, int len) { var b = new byte[5 + len]; b[0] = File; b[1] = (byte)(id >> 24); b[2] = (byte)(id >> 16); b[3] = (byte)(id >> 8); b[4] = (byte)id; Buffer.BlockCopy(data, off, b, 5, len); return b; }
    }

    public class Tile { public int X, Y, W, H; public byte[] Jpeg; }
    public class FrameMsg
    {
        public int Mon, SrcW, SrcH, EncW, EncH, Flags; public uint Seq; public List<Tile> Tiles = new List<Tile>();
        public const long MaxPixels = 8192L * 8192;
        public byte[] Encode()
        {
            var m = new MemoryStream();
            m.WriteByte(Msg.Frame); m.WriteByte((byte)Mon); Msg.U16(m, SrcW); Msg.U16(m, SrcH); Msg.U16(m, EncW); Msg.U16(m, EncH); Msg.U32(m, Seq); m.WriteByte((byte)Flags); Msg.U16(m, Tiles.Count);
            foreach (var t in Tiles) { Msg.U16(m, t.X); Msg.U16(m, t.Y); Msg.U16(m, t.W); Msg.U16(m, t.H); Msg.U32(m, t.Jpeg.Length); m.Write(t.Jpeg, 0, t.Jpeg.Length); }
            return m.ToArray();
        }
        public static FrameMsg Parse(byte[] b)
        {
            if (b.Length < 17 || b[0] != Msg.Frame) throw new InvalidDataException("not a frame");
            var f = new FrameMsg { Mon = b[1], SrcW = Msg.RU16(b, 2), SrcH = Msg.RU16(b, 4), EncW = Msg.RU16(b, 6), EncH = Msg.RU16(b, 8), Seq = Msg.RU32(b, 10), Flags = b[14] };
            // v1.1: the viewer allocates EncW x EncH x 4 bytes, so a frame claiming a giant picture is refused (8K is 7680 x 4320)
            if (f.EncW == 0 || f.EncH == 0 || (long)f.EncW * f.EncH > MaxPixels) throw new InvalidDataException("picture size out of range");
            int n = Msg.RU16(b, 15), o = 17;
            for (int i = 0; i < n; i++)
            {
                if (o + 12 > b.Length) throw new InvalidDataException("frame cut short");
                var t = new Tile { X = Msg.RU16(b, o), Y = Msg.RU16(b, o + 2), W = Msg.RU16(b, o + 4), H = Msg.RU16(b, o + 6) }; var len = (int)Msg.RU32(b, o + 8); o += 12;
                if (len < 0 || o + len > b.Length) throw new InvalidDataException("frame cut short");
                t.Jpeg = Kdf.Slice(b, o, len); o += len; f.Tiles.Add(t);
            }
            return f;
        }
    }
}

// Hallow Assist picture coding: the frame is split into 64x64 blocks, changed blocks next to each other in a row are joined
// into strips, and each strip is sent as a JPEG (simple, and every browser can draw it). The viewer pastes strips into its copy.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace HallowAssist
{
    public class TileEncoder
    {
        public const int Block = 64;
        byte[] prev; int pw, ph;
        static readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        public void Reset() { prev = null; }

        // frame: BGRX w x h. dirty: changed areas if known (null = compare every block). full: send everything.
        public unsafe List<Tile> Encode(byte[] frame, int w, int h, int quality, bool full, List<Rectangle> dirty)
        {
            if (prev == null || pw != w || ph != h) { prev = new byte[w * h * 4]; pw = w; ph = h; full = true; }
            int bx = (w + Block - 1) / Block, by = (h + Block - 1) / Block;
            var changed = new bool[bx * by];
            bool[] candidate = null;
            if (!full && dirty != null)
            {
                candidate = new bool[bx * by];
                foreach (var r0 in dirty)
                {
                    var r = Rectangle.Intersect(r0, new Rectangle(0, 0, w, h)); if (r.IsEmpty) continue;
                    for (int y = r.Top / Block; y <= (r.Bottom - 1) / Block; y++) for (int x = r.Left / Block; x <= (r.Right - 1) / Block; x++) candidate[y * bx + x] = true;
                }
            }
            fixed (byte* f = frame) fixed (byte* p = prev)
            {
                for (int j = 0; j < by; j++)
                    for (int i = 0; i < bx; i++)
                    {
                        int k = j * bx + i;
                        if (full) { changed[k] = true; continue; }
                        if (candidate != null && !candidate[k]) continue;
                        int x0 = i * Block, y0 = j * Block, bw = Math.Min(Block, w - x0), bh = Math.Min(Block, h - y0);
                        for (int y = y0; y < y0 + bh && !changed[k]; y++)
                        {
                            long* a = (long*)(f + (y * w + x0) * 4), b = (long*)(p + (y * w + x0) * 4);
                            int n = bw / 2;
                            for (int q = 0; q < n; q++) if (a[q] != b[q]) { changed[k] = true; break; }
                            if (!changed[k] && (bw & 1) == 1 && ((int*)a)[bw - 1] != ((int*)b)[bw - 1]) changed[k] = true;
                        }
                    }
            }
            var tiles = new List<Tile>();
            var handle = GCHandle.Alloc(frame, GCHandleType.Pinned);
            try
            {
                for (int j = 0; j < by; j++)
                {
                    int i = 0;
                    while (i < bx)
                    {
                        if (!changed[j * bx + i]) { i++; continue; }
                        int s = i; while (i < bx && changed[j * bx + i]) i++;
                        int x0 = s * Block, y0 = j * Block, rw = Math.Min(i * Block, w) - x0, rh = Math.Min(Block, h - y0);
                        tiles.Add(new Tile { X = x0, Y = y0, W = rw, H = rh, Jpeg = Jpeg(handle.AddrOfPinnedObject() + (y0 * w + x0) * 4, w * 4, rw, rh, quality) });
                        for (int y = y0; y < y0 + rh; y++) Buffer.BlockCopy(frame, (y * w + x0) * 4, prev, (y * w + x0) * 4, rw * 4);
                    }
                }
            }
            finally { handle.Free(); }
            return tiles;
        }

        static byte[] Jpeg(IntPtr scan0, int stride, int w, int h, int quality)
        {
            using (var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, scan0))
            using (var ms = new MemoryStream())
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(10, Math.Min(95, quality)));
                bmp.Save(ms, jpeg, ep);
                return ms.ToArray();
            }
        }
    }

    // the viewer's copy of their screen
    public class FrameBuffer
    {
        public byte[] Pixels = new byte[0]; public int W, H;
        public void Resize(int w, int h) { if (w != W || h != H) { W = w; H = h; Pixels = new byte[w * h * 4]; } }
        // pastes one JPEG strip; returns the area it covered
        public Rectangle Apply(Tile t)
        {
            using (var ms = new MemoryStream(t.Jpeg))
            using (var img = new Bitmap(ms))
            {
                int w = Math.Min(img.Width, W - t.X), h = Math.Min(img.Height, H - t.Y);
                if (w <= 0 || h <= 0 || t.X < 0 || t.Y < 0) return Rectangle.Empty;
                var bd = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try { for (int y = 0; y < h; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, Pixels, ((t.Y + y) * W + t.X) * 4, w * 4); }
                finally { img.UnlockBits(bd); }
                return new Rectangle(t.X, t.Y, w, h);
            }
        }
    }

    // picture quality: JPEG quality and scale. "auto" moves between the levels by how quickly frames are acknowledged.
    public static class Quality
    {
        public static readonly int[] Jpeg = { 85, 72, 60, 50, 40 };
        public static readonly double[] Scale = { 1.0, 1.0, 1.0, 0.75, 0.5 };
        public static readonly string[] Label = { "best", "good", "balanced", "fast", "fastest" };
        public static int Fixed(string q) { return q == "best" ? 0 : q == "balanced" ? 2 : q == "fast" ? 4 : -1; }
    }
}

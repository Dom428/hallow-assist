// Hallow Assist unit tests (Windows side): crypto vectors, the sealed channel, tile coding round trip, input mapping across
// monitors with negative coordinates, protocol parsing, unattended defaults. Nothing here touches the real screen, mouse,
// keyboard, clipboard or settings - except "--dxgi-smoke", which reads ONE frame of the screen into memory and throws it away.
//   tests\AssistTests.exe [--dxgi-smoke]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;

namespace HallowAssist.Tests
{
    public static class Run
    {
        static int pass, fail;
        static void Ok(bool c, string name) { if (c) { pass++; Console.WriteLine("ok   " + name); } else { fail++; Console.WriteLine("FAIL " + name); } }
        static byte[] H(string hex) { hex = hex.Replace(" ", ""); var b = new byte[hex.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16); return b; }
        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }

        public static int Main(string[] args)
        {
            if (args.Contains("--dxgi-smoke")) return DxgiSmoke();
            if (args.Length >= 2 && args[0] == "--persist") { Console.WriteLine(Kdf.PersistentCode(args[1])); return 0; }
            if (args.Length >= 3 && args[0] == "--pwkey") { Console.WriteLine(Hex(Kdf.PasswordKey(args[1], args[2]))); return 0; }
            try { Crypto(); Channels(); Tiles(); Inputs(); Protocol(); Misc(); }
            catch (Exception e) { fail++; Console.WriteLine("FAIL exception: " + e); }
            Console.WriteLine(pass + " passed, " + fail + " failed");
            return fail == 0 ? 0 : 1;
        }

        static void Crypto()
        {
            // HKDF-SHA256, RFC 5869 test case 1
            var okm = Kdf.Hkdf(H("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"), H("000102030405060708090a0b0c"), H("f0f1f2f3f4f5f6f7f8f9"), 42);
            Ok(Hex(okm) == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", "HKDF-SHA256 matches RFC 5869 test case 1");
            // PBKDF2-HMAC-SHA256, the well-known 4096-iteration vector
            using (var p = new Rfc2898DeriveBytes(Kdf.Utf8("passwordPASSWORDpassword"), Kdf.Utf8("saltSALTsaltSALTsaltSALTsaltSALTsalt"), 4096, HashAlgorithmName.SHA256))
                Ok(Hex(p.GetBytes(40)) == "348c89dbcbd32b2f32d814b8116e84cf2b17347ebc1800181c4e2a1fb8dd53e1c635518c7dac47e9", "PBKDF2-SHA256 matches the standard vector");
            // AES-256-GCM, test case 16 of the GCM specification (with associated data)
            using (var g = new AesGcm(H("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308")))
            {
                var pt = H("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39");
                var ct = g.Seal(H("cafebabefacedbaddecaf888"), pt, 0, pt.Length, H("feedfacedeadbeeffeedfacedeadbeefabaddad2"));
                Ok(Hex(ct) == "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662" + "76fc6ece0f4e1768cddf8853bb2d551b", "AES-256-GCM matches the GCM spec test case 16");
                var back = g.Open(H("cafebabefacedbaddecaf888"), ct, 0, ct.Length, H("feedfacedeadbeeffeedfacedeadbeefabaddad2"));
                Ok(back != null && Hex(back) == Hex(pt), "AES-256-GCM opens its own ciphertext");
                ct[5] ^= 1; Ok(g.Open(H("cafebabefacedbaddecaf888"), ct, 0, ct.Length, H("feedfacedeadbeeffeedfacedeadbeefabaddad2")) == null, "AES-256-GCM refuses a changed ciphertext");
            }
            // ECDH both ways gives the same secret
            using (var a = new EcdhKey()) using (var b = new EcdhKey())
            {
                Ok(a.Pub.Length == 65 && a.Pub[0] == 4, "ECDH public key is a 65-byte uncompressed P-256 point");
                Ok(Kdf.Equal(a.SharedHash(b.Pub), b.SharedHash(a.Pub)), "ECDH: both ends agree");
                var bad = (byte[])b.Pub.Clone(); bad[40] ^= 0x55; bool threw = false; try { a.SharedHash(bad); } catch { threw = true; }
                Ok(threw, "ECDH refuses a point that isn't on the curve");
            }
            Ok(Kdf.NormPassword(" k7m x2p ") == "K7MX2P" && Kdf.NormPassword("k7m-x2p") == "K7MX2P" && Kdf.NormPassword("My Long Password 9") == "My Long Password 9", "password normalisation matches the browser");
            var pws = Enumerable.Range(0, 200).Select(i => Kdf.NewPassword()).ToList();
            Ok(pws.All(p => p.Length == 6 && p.All(ch => "23456789ABCDEFGHJKMNPQRSTUVWXYZ".IndexOf(ch) >= 0)) && pws.Distinct().Count() > 190, "one-time passwords: 6 characters, no look-alikes, random");
            var code = Kdf.PersistentCode(new string('a', 64)); Ok(code.Length == 9 && code[0] != '0', "persistent code has 9 digits");
        }

        static void Pair(out SessionKeys v, out SessionKeys h, string pwV = "ABCDEF", string pwH = "ABCDEF")
        {
            using (var a = new EcdhKey()) using (var b = new EcdhKey())
            {
                var nonce = new byte[16]; nonce[3] = 9;
                v = SessionKeys.Derive(a.SharedHash(b.Pub), Kdf.PasswordKey(pwV, "123456789"), "123456789", a.Pub, b.Pub, nonce);
                h = SessionKeys.Derive(b.SharedHash(a.Pub), Kdf.PasswordKey(pwH, "123 456 789"), "123456789", a.Pub, b.Pub, nonce);
            }
        }
        static void Channels()
        {
            SessionKeys v, h; Pair(out v, out h);
            Ok(Kdf.Equal(v.ConfirmV, h.ConfirmV) && v.Safety == h.Safety && v.Safety.Length == 4, "both ends derive the same keys and safety code");
            SessionKeys v2, h2; Pair(out v2, out h2, "ABCDEF", "ABCDEG");
            Ok(!Kdf.Equal(v2.ConfirmV, h2.ConfirmV), "wrong password: key confirmation fails");
            var cv = new Channel(v, false); var ch = new Channel(h, true);
            var m1 = cv.Seal(Kdf.Utf8("one")); var m2 = cv.Seal(Kdf.Utf8("two")); var m3 = cv.Seal(Kdf.Utf8("three"));
            Ok(System.Text.Encoding.UTF8.GetString(ch.Open(m1)) == "one", "sealed message opens");
            Ok(Throws(() => ch.Open(m1), "replayed"), "replayed message refused");
            Ok(Throws(() => ch.Open(m3), "out of order"), "out-of-order message refused");
            var t = (byte[])m2.Clone(); t[t.Length - 3] ^= 1;
            Ok(Throws(() => ch.Open(t), "changed"), "tampered message refused");
            Ok(System.Text.Encoding.UTF8.GetString(ch.Open(m2)) == "two", "the real message still opens after a refused fake");
            var hdr = (byte[])m3.Clone(); hdr[8] = 4;   // counter 3 -> 4 in the header (authenticated)
            Ok(Throws(() => ch.Open(hdr), ""), "a changed counter is refused");
            var back = ch.Seal(Kdf.Utf8("back"));
            Ok(Throws(() => new Channel(h, true).Open(back), "changed"), "a message for the other direction doesn't open");
            Ok(System.Text.Encoding.UTF8.GetString(cv.Open(back)) == "back", "host -> viewer works");
        }
        static bool Throws(Action a, string contains) { try { a(); return false; } catch (CryptographicException e) { return e.Message.Contains(contains); } }

        static void Tiles()
        {
            var src = new SyntheticSource(); List<Rectangle> d;
            src.Capture(0, 1.0, out d);
            var enc = new TileEncoder();
            var tiles = enc.Encode(src.Frame, src.FrameW, src.FrameH, 85, true, null);
            Ok(tiles.Count == (720 + 63) / 64, "full frame = one strip per block row (" + tiles.Count + ")");
            var fb = new FrameBuffer(); fb.Resize(src.FrameW, src.FrameH);
            var msg = new FrameMsg { Mon = 0, SrcW = 1280, SrcH = 720, EncW = src.FrameW, EncH = src.FrameH, Seq = 1, Flags = 1, Tiles = tiles }.Encode();
            var parsed = FrameMsg.Parse(msg);
            foreach (var t in parsed.Tiles) fb.Apply(t);
            Ok(MeanDiff(src.Frame, fb.Pixels) < 3.0, "tile round trip: decoded picture matches (mean diff " + MeanDiff(src.Frame, fb.Pixels).ToString("0.00") + ")");
            // next frame: only the moving box changes
            src.Capture(0, 1.0, out d);
            var t2 = enc.Encode(src.Frame, src.FrameW, src.FrameH, 85, false, null);
            Ok(t2.Count > 0 && t2.Count <= 4 && t2.All(t => t.Y >= 448 && t.Y < 640), "next frame sends only the changed strips (" + t2.Count + ")");
            foreach (var t in FrameMsg.Parse(new FrameMsg { EncW = src.FrameW, EncH = src.FrameH, Tiles = t2 }.Encode()).Tiles) fb.Apply(t);
            Ok(MeanDiff(src.Frame, fb.Pixels) < 3.0, "after the update the copies match again");
            var t3 = enc.Encode(src.Frame, src.FrameW, src.FrameH, 85, false, null);
            Ok(t3.Count == 0, "unchanged frame sends nothing");
            // dirty rects limit what is compared
            src.Capture(0, 1.0, out d);
            var t4 = enc.Encode(src.Frame, src.FrameW, src.FrameH, 85, false, new List<Rectangle> { new Rectangle(0, 0, 10, 10) });
            Ok(t4.Count == 0, "dirty rectangles: blocks outside them aren't compared");
            // scaled frame
            var s2 = new SyntheticSource(); s2.Capture(1, 0.5, out d);
            Ok(s2.FrameW == 512 && s2.FrameH == 384, "half-scale frame of the second monitor is 512x384");
            Ok(Throws2(() => FrameMsg.Parse(msg.Take(40).ToArray())), "a cut-short frame is refused");
        }
        static bool Throws2(Action a) { try { a(); return false; } catch { return true; } }
        static double MeanDiff(byte[] a, byte[] b) { long s = 0; int n = 0; for (int i = 0; i < a.Length; i += 4) { for (int k = 0; k < 3; k++) s += Math.Abs(a[i + k] - b[i + k]); n += 3; } return (double)s / n; }

        static void Inputs()
        {
            var mons = SyntheticSource.Mons; int px, py, ax, ay;
            InputMap.ToPixel(mons[0], 0, 0, out px, out py); Ok(px == 0 && py == 0, "monitor 1 top-left");
            InputMap.ToPixel(mons[0], 65535, 65535, out px, out py); Ok(px == 1279 && py == 719, "monitor 1 bottom-right");
            InputMap.ToPixel(mons[1], 0, 0, out px, out py); Ok(px == -1024 && py == -200, "monitor 2 (negative coordinates) top-left = (-1024,-200)");
            InputMap.ToPixel(mons[1], 65535, 65535, out px, out py); Ok(px == -1 && py == 567, "monitor 2 bottom-right = (-1,567)");
            InputMap.ToPixel(mons[1], 32768, 32768, out px, out py); Ok(px == -512 && py == 184, "monitor 2 middle = (-512,184)");
            int vx, vy, vw, vh; InputMap.Bounds(mons, out vx, out vy, out vw, out vh);
            Ok(vx == -1024 && vy == -200 && vw == 2304 && vh == 920, "virtual desktop spans both monitors");
            InputMap.ToAbsolute(-1024, -200, vx, vy, vw, vh, out ax, out ay); Ok(ax == 0 && ay == 0, "absolute: virtual top-left = 0,0");
            InputMap.ToAbsolute(1279, 719, vx, vy, vw, vh, out ax, out ay); Ok(ax == 65535 && ay == 65535, "absolute: virtual bottom-right = 65535");
            InputMap.ToAbsolute(0, 0, vx, vy, vw, vh, out ax, out ay); Ok(Math.Abs(ax - 29140) <= 1 && Math.Abs(ay - 14262) <= 1, "absolute: primary origin inside the virtual desktop (" + ax + "," + ay + ")");
            InputMap.ToPixel(mons[0], 99999, -5, out px, out py); Ok(px == 1279 && py == 0, "out-of-range positions are clamped");
        }

        static void Protocol()
        {
            var m = Msg.Button(1, true, 100, 65535);
            Ok(m[0] == Msg.Input && m[1] == Msg.InButton && m[2] == 1 && m[3] == 1 && Msg.RU16(m, 4) == 100 && Msg.RU16(m, 6) == 65535, "button message layout");
            var w = Msg.Wheel(-120, 240); Ok(Msg.RI16(w, 2) == -120 && Msg.RI16(w, 4) == 240, "wheel message keeps the sign");
            var k = Msg.Key(0xE04D, false); Ok(Msg.RU16(k, 2) == 0xE04D && k[4] == 0, "key message (extended scan code)");
            var j = Msg.ParseJson(Msg.JsonMsg("t", "chat", "text", "héllo")); Ok(Msg.Str(j, "text") == "héllo", "JSON message round trip with non-ASCII");
            Ok(Transfers.SafeName("..\\..\\evil.exe") == "evil.exe" && Transfers.SafeName("a/b/c:d?.txt") == "c_d_.txt" && Transfers.SafeName("CON.txt") == "_CON.txt" && Transfers.SafeName("") == "file", "file names from the other side are made safe");
            Ok(Transfers.SafeName("con.tar.gz") == "_con.tar.gz" && Transfers.SafeName("COM7.txt") == "_COM7.txt" && Transfers.SafeName("LPT9") == "_LPT9" && Transfers.SafeName("Console.txt") == "Console.txt" && Transfers.SafeName("COMPUTER.txt") == "COMPUTER.txt", "every reserved device name is made safe, other names are left alone");
            { var big = new FrameMsg { EncW = 65535, EncH = 65535, Seq = 1 }.Encode(); bool refused = false; try { FrameMsg.Parse(big); } catch (System.IO.InvalidDataException) { refused = true; }
              var ok4k = FrameMsg.Parse(new FrameMsg { EncW = 3840, EncH = 2160, Seq = 2 }.Encode());
              Ok(refused && ok4k.EncW == 3840, "a frame claiming a giant picture is refused; 4K still passes"); }
            Ok(Transfers.Risky("setup.EXE") && Transfers.Risky("a.lnk") && Transfers.Risky("x.ps1") && !Transfers.Risky("photo.jpg") && !Transfers.Risky("notes.txt") && Transfers.Warning("photo.jpg") == "", "programs and scripts get a warning in the file prompt");
            { var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "assist-motw-" + Guid.NewGuid().ToString("N") + ".txt"); System.IO.File.WriteAllText(tmp, "x");
              bool marked = Transfers.MarkFromInternet(tmp); string zone = null;
              try { zone = System.IO.File.ReadAllText(tmp + ":Zone.Identifier"); } catch { }
              Ok(marked && (zone == null || zone.Contains("ZoneId=3")), "received files are marked as from the internet"); try { System.IO.File.Delete(tmp); } catch { } }
        }

        static void Misc()
        {
            var s = new AssistSettings();
            Ok(!s.Unattended && s.UnattendedSecret == null, "unattended access is off by default");
            Ok(UnattendedDialog.Check("short", "short") != null && UnattendedDialog.Check("Abcdefghijk1", "Abcdefghijk1") == null && UnattendedDialog.Check("aaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaa") != null && UnattendedDialog.Check("Abcdefghijk1", "Abcdefghijk2") != null, "unattended password must be strong and typed twice");
            Ok(AssistSettings.WsUri("https://assist.example.com").ToString() == "wss://assist.example.com/ws" && AssistSettings.WsUri("assist.example.com/") .ToString() == "wss://assist.example.com/ws" &&
               AssistSettings.WsUri("http://127.0.0.1:8090").ToString() == "ws://127.0.0.1:8090/ws" && AssistSettings.WsUri("http://assist.example.com") == null && AssistSettings.WsUri("ftp://x") == null, "relay address: https -> wss, plain http only locally");
            Ok(!Desktop.SecureDesktopActive(), "secure desktop isn't active right now (normal desktop)");
        }

        static int DxgiSmoke()
        {
            // one frame of the primary monitor into memory, then discarded; nothing is saved or sent
            using (var s = new DxgiSource())
            {
                List<Rectangle> d; bool got = false;
                for (int i = 0; i < 5 && !got; i++) got = s.Capture(0, 1.0, out d);
                long sum = 0; if (s.Frame != null) for (int i = 0; i < s.Frame.Length; i += 997) sum += s.Frame[i];
                Console.WriteLine("dxgi-smoke: kind=" + s.Kind + " size=" + s.FrameW + "x" + s.FrameH + " got=" + got + " nonblack=" + (sum > 0) + (s.LastError != null ? " lastError=" + s.LastError : ""));
                return got ? 0 : 1;
            }
        }
    }
}

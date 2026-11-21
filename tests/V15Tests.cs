// Hallow Assist 1.5 unit tests: the password from Hallow Keys (against a stand-in hk.exe, every exit code - never the real
// Hallow Keys), copied files through two file-transfer ends wired to each other in memory (one question per batch, the Mark of
// the Web, device names, limits, an older app on the other side), and the recorder writing an AVI (and an MP4 when Hallow
// Clips' FFmpeg is on this PC) from the synthetic picture. Everything goes into a temporary folder that is removed afterwards.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetFileAttributesW(string name);
        static bool HasMotw(string path) { return GetFileAttributesW(path + ":Zone.Identifier") != 0xFFFFFFFF; }

        static void V15()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "HallowAssistTests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try { V15Keys(tmp); V15Files(tmp); V15Recorder(tmp); }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        // ---------------- Hallow Keys
        static void V15Keys(string tmp)
        {
            foreach (var pw in new[] { "Correct-Horse-42!", "  a long pass phrase 99 \r\n", "k7m x2p", "abc-def", "pässwörd-ünicode-123", "\tTabbed-Password-77\t" })
            {
                var ch = pw.ToCharArray();
                Ok(Kdf.Equal(Kdf.PasswordKey(ch, ch.Length, "715 390 246"), Kdf.PasswordKey(pw, "715390246")), "Keys: the key from a char array matches the string version for \"" + pw.Trim() + "\"");
            }
            var hk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "standin", "hk.exe");
            Ok(File.Exists(hk), "Keys: the stand-in hk.exe was built (tests\\standin\\hk.exe)");
            if (!File.Exists(hk)) return;
            var items = Path.Combine(tmp, "items.txt"); var log = Path.Combine(tmp, "hk-calls.log");
            const string fake = "Made-Up-Test-Pass-42!";
            File.WriteAllText(items, "Office PC\t" + fake + "\nOffice \"PC\" \\ 2\\\t" + fake + "x\n", new UTF8Encoding(false));
            Environment.SetEnvironmentVariable("HK_STANDIN_ITEMS", items); Environment.SetEnvironmentVariable("HK_STANDIN_LOG", log);
            const string code = "715390246";
            var r = HallowKeys.GetKey(hk, "Office PC", code, 15000);
            Ok(r.Code == 0 && r.Ok && Kdf.Equal(r.Key, Kdf.PasswordKey(fake, code)), "Keys: exit 0 - the session key is made from the password it printed");
            Ok(!r.Message.Contains(fake), "Keys: the message never contains the password");
            var calls = File.ReadAllLines(log);
            Ok(calls.Last() == "--get | Office PC | --field | password", "Keys: runs hk --get \"<item>\" --field password (got: " + calls.Last() + ")");
            var odd = "Office \"PC\" \\ 2\\";
            var r2 = HallowKeys.GetKey(hk, odd, code, 15000);
            Ok(r2.Ok && Kdf.Equal(r2.Key, Kdf.PasswordKey(fake + "x", code)) && File.ReadAllLines(log).Last() == "--get | " + odd + " | --field | password", "Keys: an item name with quotes and backslashes reaches hk.exe unchanged");
            var expect = new Dictionary<int, string> {
                { 1, "didn't understand" }, { 2, "Start Hallow Keys and try again" }, { 3, "Unlock Hallow Keys and try again" }, { 4, "has no item called" },
                { 5, "Hallow Keys said no" }, { 6, "Turn on the command line in Keys > Settings > Developer tools" }, { 7, "couldn't give the password" } };
            foreach (var kv in expect)
            {
                var e = HallowKeys.GetKey(hk, "code-" + kv.Key, code, 15000);
                Ok(e.Code == kv.Key && e.Key == null && e.Message.Contains(kv.Value), "Keys: exit " + kv.Key + " -> \"" + e.Message + "\"");
            }
            var x = HallowKeys.GetKey(hk, "code-9", code, 15000);
            Ok(x.Code == 9 && x.Key == null && x.Message.Contains("error 9"), "Keys: an unknown exit code is an error, no key");
            var nf = HallowKeys.GetKey(hk, "No such item", code, 15000);
            Ok(nf.Code == 4 && nf.Message.Contains("“No such item”"), "Keys: an item that isn't there -> not found, named in the message");
            var em = HallowKeys.GetKey(hk, "empty", code, 15000);
            Ok(em.Code == HallowKeys.Empty && em.Key == null, "Keys: an empty password is refused");
            var sw = Stopwatch.StartNew(); var hang = HallowKeys.GetKey(hk, "hang", code, 1500);
            Ok(hang.Code == HallowKeys.TimedOut && hang.Key == null && sw.ElapsedMilliseconds < 8000, "Keys: no answer in time -> stopped after the timeout (" + sw.ElapsedMilliseconds + " ms)");
            Thread.Sleep(300);
            Ok(!Process.GetProcessesByName("hk").Any(p => { try { return string.Equals(p.MainModule.FileName, hk, StringComparison.OrdinalIgnoreCase); } catch { return false; } }), "Keys: the timed-out hk.exe was ended");
            var miss = HallowKeys.GetKey(Path.Combine(tmp, "nowhere", "hk.exe"), "Office PC", code, 15000);
            Ok(miss.Code == HallowKeys.Missing && miss.Message.Contains("2.6 or newer isn't installed"), "Keys: no hk.exe -> says Hallow Keys 2.6+ isn't installed");
            int before = File.ReadAllLines(log).Length;
            Ok(HallowKeys.GetKey(hk, "--run", code, 15000).Code == HallowKeys.BadItem && HallowKeys.GetKey(hk, "two\nlines", code, 15000).Code == HallowKeys.BadItem && HallowKeys.GetKey(hk, "  ", code, 15000).Code == HallowKeys.BadItem, "Keys: names that start with \"-\", have line breaks or are empty are refused");
            Ok(File.ReadAllLines(log).Length == before, "Keys: ...and hk.exe isn't even started for them");
            Ok(HallowKeys.Quote("a b") == "\"a b\"" && HallowKeys.Quote("plain") == "plain" && HallowKeys.Quote("x\\\"y") == "\"x\\\\\\\"y\"" && HallowKeys.Quote("end\\ ") == "\"end\\ \"", "Keys: command-line quoting");
            var cleaned = AssistSettings.CleanPcs(new List<SavedPc> { new SavedPc { Name = "A", Code = "715390246", KeysItem = "  Office PC  " }, new SavedPc { Name = "B", Code = "302118457", KeysItem = "--bad" } });
            Ok(cleaned[0].KeysItem == "Office PC" && cleaned[1].KeysItem == null, "Keys: saved item names are tidied; unusable ones dropped");
            Environment.SetEnvironmentVariable("HK_STANDIN_ITEMS", null); Environment.SetEnvironmentVariable("HK_STANDIN_LOG", null);
        }

        // ---------------- copied files: two transfer ends wired to each other
        class Pair2
        {
            public Transfers A, B; public readonly List<string> NotesA = new List<string>(), NotesB = new List<string>();
            public readonly List<string> SentA = new List<string>(), SentB = new List<string>();
        }
        static Pair2 Wire(string dirA, string dirB)
        {
            var p = new Pair2();
            Transfers a = null, b = null;
            Action<Transfers, byte[]> deliver = (to, m) => { if (m[0] == Msg.Json) to.Handle(Msg.ParseJson(m)); else if (m[0] == Msg.File) to.Chunk(m); };
            a = new Transfers(m => { if (m[0] == Msg.Json) lock (p.SentA) p.SentA.Add(Msg.Str(Msg.ParseJson(m), "t")); deliver(b, m); }, () => 0, () => true, dirA);
            b = new Transfers(m => { if (m[0] == Msg.Json) lock (p.SentB) p.SentB.Add(Msg.Str(Msg.ParseJson(m), "t")); deliver(a, m); }, () => 0, () => true, dirB);
            a.Note += s => { lock (p.NotesA) p.NotesA.Add(s); }; b.Note += s => { lock (p.NotesB) p.NotesB.Add(s); };
            a.PeerBatches = b.PeerBatches = true; p.A = a; p.B = b;
            return p;
        }
        static bool Wait(Func<bool> f, int ms = 8000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(20); } return f(); }

        static void V15Files(string tmp)
        {
            var src = Path.Combine(tmp, "src"); var dA = Path.Combine(tmp, "helper-dl"); var dB = Path.Combine(tmp, "pc-dl");
            Directory.CreateDirectory(src); Directory.CreateDirectory(Path.Combine(src, "a folder"));
            var f1 = Path.Combine(src, "notes.txt"); File.WriteAllText(f1, "copied file one");
            var f2 = Path.Combine(src, "data.bin"); File.WriteAllBytes(f2, Enumerable.Range(0, 150000).Select(i => (byte)(i % 253)).ToArray());
            var p = Wire(dA, dB);
            int asked = 0; string[] askedNames = null; bool askedClip = false; string[] got = null; bool gotClip = false;
            p.B.OfferedMany += (names, total, clip, answer) => { asked++; askedNames = names; askedClip = clip; answer(true); };
            p.B.BatchReceived += (paths, clip) => { got = paths; gotClip = clip; };
            var err = p.A.OfferMany(new[] { f1, f2, Path.Combine(src, "a folder") }, true);
            Ok(err == null && p.NotesA.Any(n => n.Contains("Folders aren't sent")), "copied files: offered as one batch; the folder is skipped and that's said");
            Ok(Wait(() => got != null), "copied files: helper -> PC arrive after the PC accepts");
            Ok(asked == 1 && askedNames.SequenceEqual(new[] { "notes.txt", "data.bin" }) && askedClip, "copied files: ONE question for the whole batch, naming the files");
            Ok(got != null && got.Length == 2 && gotClip && got.All(g => g.StartsWith(dB)) && File.ReadAllText(Path.Combine(dB, "notes.txt")) == "copied file one" && File.ReadAllBytes(Path.Combine(dB, "data.bin")).SequenceEqual(File.ReadAllBytes(f2)), "copied files: both files complete in the PC's folder, handed over for its clipboard");
            Ok(got != null && got.All(HasMotw), "copied files: each one gets the Mark of the Web (SmartScreen)");
            Ok(!Directory.GetFiles(dB, "*.part").Any(), "copied files: no .part files left");

            // the other way, declined, then accepted
            bool answerB = false; int askedA = 0; string[] gotA = null;
            p.A.OfferedMany += (names, total, clip, answer) => { askedA++; answer(answerB); };
            p.A.BatchReceived += (paths, clip) => { gotA = paths; };
            var f3 = Path.Combine(src, "from-pc.txt"); File.WriteAllText(f3, "the other way");
            Ok(p.B.OfferMany(new[] { f3 }, true) == null && Wait(() => askedA == 1) && Wait(() => p.NotesB.Any(n => n.Contains("declined"))), "copied files: PC -> helper, declined: they're told");
            Thread.Sleep(200);
            Ok(!Directory.Exists(dA) || Directory.GetFiles(dA).Length == 0, "copied files: nothing saved after Decline");
            answerB = true;
            Ok(p.B.OfferMany(new[] { f3 }, true) == null && Wait(() => gotA != null) && File.ReadAllText(gotA[0]) == "the other way" && HasMotw(gotA[0]), "copied files: PC -> helper, accepted: arrives with the Mark of the Web");

            // names and limits on the receiving side, whatever the sender claims
            string[] seen = null; int asks = 0; var declines = new List<uint>();
            var t = new Transfers(m => { if (m[0] == Msg.Json) { var d = Msg.ParseJson(m); if (Msg.Str(d, "t") == "file-decline") declines.Add((uint)Msg.Num(d, "id")); } }, () => 0, () => true, Path.Combine(tmp, "x"));
            t.OfferedMany += (names, total, clip, answer) => { asks++; seen = names; answer(false); };
            Func<int, long, object[]> many = (n, size) => Enumerable.Range(1, n).Select(i => (object)new Dictionary<string, object> { { "id", i }, { "name", "f" + i + ".txt" }, { "size", size } }).ToArray();
            t.Handle(Msg.FromJson(Msg.ToJson(new Dictionary<string, object> { { "t", "file-batch" }, { "clip", true }, { "files", new object[] {
                new Dictionary<string, object> { { "id", 1 }, { "name", "CON.tar.gz" }, { "size", 10 } }, new Dictionary<string, object> { { "id", 2 }, { "name", "..\\..\\evil.txt" }, { "size", 10 } },
                new Dictionary<string, object> { { "id", 3 }, { "name", "com7 .txt" }, { "size", 10 } } } } })));
            Ok(asks == 1 && seen.SequenceEqual(new[] { "_CON.tar.gz", "evil.txt", "_com7 .txt" }), "copied files: device names and paths are made safe before the question (" + string.Join(", ", seen ?? new string[0]) + ")");
            Ok(declines.Count == 3, "copied files: Decline answers every file of the batch");
            declines.Clear();
            t.Handle(Msg.FromJson(Msg.ToJson(new Dictionary<string, object> { { "t", "file-batch" }, { "files", many(Transfers.MaxBatch + 1, 1) } })));
            Ok(asks == 1 && declines.Count == Transfers.MaxBatch + 1, "copied files: more than " + Transfers.MaxBatch + " files are declined without asking");
            declines.Clear();
            t.Handle(Msg.FromJson(Msg.ToJson(new Dictionary<string, object> { { "t", "file-batch" }, { "files", many(2, 3L * 1024 * 1024 * 1024) } })));
            Ok(asks == 1 && declines.Count == 2, "copied files: over 4 GB together is declined without asking");
            declines.Clear();
            t.Handle(Msg.FromJson(Msg.ToJson(new Dictionary<string, object> { { "t", "file-batch" }, { "files", new object[] { new Dictionary<string, object> { { "id", 7 }, { "name", "big.iso" }, { "size", Transfers.MaxSize + 1 } } } } })));
            Ok(asks == 1 && declines.Count == 1, "copied files: a file over 4 GB is declined without asking");
            Ok(Transfers.BatchText(new[] { "a.txt", "run.exe" }, 2048, "D", true).Contains("programs, scripts or shortcuts (run.exe)") && Transfers.BatchText(new[] { "a.txt" }, 10, "D", true).Contains("clipboard"), "copied files: the question warns about programs and says they go on the clipboard");

            // sender-side limits
            var lots = Path.Combine(tmp, "lots"); Directory.CreateDirectory(lots);
            var lotsOf = Enumerable.Range(0, Transfers.MaxBatch + 1).Select(i => { var f = Path.Combine(lots, i + ".txt"); File.WriteAllText(f, "x"); return f; }).ToArray();
            int sentBefore = p.SentA.Count;
            var e2 = p.A.OfferMany(lotsOf, true);
            Ok(e2 != null && e2.Contains("at most " + Transfers.MaxBatch) && p.SentA.Count == sentBefore, "copied files: more than " + Transfers.MaxBatch + " copied files aren't offered at all");
            Ok(p.A.OfferMany(new string[0], true).StartsWith("Nothing is copied"), "copied files: nothing copied -> says so");

            // an older app on the other side: one question per file, nothing for its clipboard
            var q = Wire(Path.Combine(tmp, "old-a"), Path.Combine(tmp, "old-b")); q.A.PeerBatches = false;
            int singles = 0; bool batchFired = false;
            q.B.Offered += (n, s, answer) => { singles++; answer(true); };
            q.B.OfferedMany += (n, s, c, answer) => { batchFired = true; answer(true); };
            Ok(q.A.OfferMany(new[] { f1, f2 }, true) == null && Wait(() => File.Exists(Path.Combine(tmp, "old-b", "data.bin")) && new FileInfo(Path.Combine(tmp, "old-b", "data.bin")).Length == 150000), "copied files: to an older app they go as ordinary files");
            Ok(singles == 2 && !batchFired && q.SentA.Count(s => s == "file-offer") == 2, "copied files: ...with one question each (the old way)");
        }

        // ---------------- the recorder
        static RecFrame Synthetic(SyntheticSource s, ref long ver)
        {
            List<System.Drawing.Rectangle> d; s.Capture(0, 1, out d); ver++;
            return new RecFrame { Pixels = (byte[])s.Frame.Clone(), W = s.FrameW, H = s.FrameH, Version = ver };
        }
        static short[] Tone(int n, ref int phase) { var s = new short[n]; for (int i = 0; i < n; i++, phase++) s[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * phase / 16000.0)); return s; }
        static void V15Recorder(string tmp)
        {
            var dir = Path.Combine(tmp, "Videos");
            var src = new SyntheticSource(); long ver = 0; long lastGrab = 0;
            Func<long, RecFrame> grab = v => { if (Environment.TickCount - lastGrab < 200) return null; lastGrab = Environment.TickCount; return Synthetic(src, ref ver); };
            var idle = new SessionRecorder(dir, "Test PC", null, grab);
            Thread.Sleep(300);
            Ok(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0, "recording: nothing is written before it's started");
            var rec = new SessionRecorder(dir, "Test PC", null, grab);
            Ok(rec.Start() == null, "recording: starts (AVI - no FFmpeg given)");
            Ok(Wait(() => rec.Started, 5000), "recording: the first frame is written");
            int ph = 0; var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1800) { rec.AddAudio(Tone(640, ref ph)); Thread.Sleep(40); }
            var path = rec.Stop();
            Ok(path != null && path.EndsWith(".avi") && File.Exists(path) && Path.GetFileName(path).StartsWith("Hallow Assist ") && path.Contains("Test PC"), "recording: an AVI named after the session (" + (path == null ? "none" : Path.GetFileName(path)) + ")");
            if (path != null && File.Exists(path))
            {
                var b = File.ReadAllBytes(path);
                Func<int, string> cc = o => Encoding.ASCII.GetString(b, o, 4);
                Ok(cc(0) == "RIFF" && cc(8) == "AVI " && BitConverter.ToUInt32(b, 4) == b.Length - 8, "recording: RIFF AVI with the right size");
                Ok(cc(12) == "LIST" && cc(20) == "hdrl" && cc(24) == "avih", "recording: the headers come first");
                int frames = BitConverter.ToInt32(b, 32 + 16), streams = BitConverter.ToInt32(b, 32 + 24), w = BitConverter.ToInt32(b, 32 + 32), h = BitConverter.ToInt32(b, 32 + 36);
                Ok(frames >= 14 && frames <= 24 && streams == 2 && w == 1280 && h == 720, "recording: ~10 fps for 1.8 s, picture and sound streams, 1280 x 720 (" + frames + " frames, " + streams + " streams)");
                int movi = 12 + 4096;
                Ok(cc(movi) == "LIST" && cc(movi + 8) == "movi", "recording: the movi list follows the reserved header space");
                int first = IndexOf(b, Encoding.ASCII.GetBytes("00dc"), movi);
                Ok(first > 0 && BitConverter.ToInt32(b, first + 4) > 1000 && b[first + 8] == 0xFF && b[first + 9] == 0xD8, "recording: the first video chunk is a JPEG picture");
                Ok(IndexOf(b, Encoding.ASCII.GetBytes("01wb"), movi) > 0 && IndexOf(b, Encoding.ASCII.GetBytes("auds"), 0) > 0, "recording: their sound is in it");
                Ok(IndexOf(b, Encoding.ASCII.GetBytes("idx1"), movi) > 0, "recording: it has an index (players can seek)");
                Ok(rec.FramesNew < rec.FramesWritten, "recording: an unchanged picture is stored as a repeat, not again (" + rec.FramesNew + " of " + rec.FramesWritten + ")");
            }
            // MP4 with Hallow Clips' FFmpeg, when it's on this PC (it is never downloaded by Hallow Assist)
            var ff = SessionRecorder.FindFfmpeg();
            if (ff == null) Console.WriteLine("skip recording: MP4 (Hallow Clips' FFmpeg isn't on this PC - the AVI path is the one used here)");
            else
            {
                var r2 = new SessionRecorder(dir, "Test PC", ff, grab);
                r2.Start(); Wait(() => r2.Started, 5000);
                sw.Restart(); while (sw.ElapsedMilliseconds < 2000) { r2.AddAudio(Tone(640, ref ph)); Thread.Sleep(40); }
                var p2 = r2.Stop();
                var ok = p2 != null && p2.EndsWith(".mp4") && File.Exists(p2) && new FileInfo(p2).Length > 1000;
                if (ok) { var head = new byte[12]; using (var f = File.OpenRead(p2)) f.Read(head, 0, 12); ok = Encoding.ASCII.GetString(head, 4, 4) == "ftyp"; }
                Ok(ok && r2.Error == null, "recording: an MP4 with Hallow Clips' FFmpeg (" + (p2 == null ? "none" : Path.GetFileName(p2)) + (r2.Error != null ? ", " + r2.Error : "") + ")");
                Ok(!Directory.GetFiles(dir, "*.part.*").Any(), "recording: FFmpeg's temporary files are gone");
            }
            Ok(SessionRecorder.SafeLabel("Mum's PC: \"main\"") == "Mum's PC_ _main_" && SessionRecorder.SafeLabel("CON") == "_CON", "recording: the PC's name is made safe for a file name");
        }
        static int IndexOf(byte[] b, byte[] pat, int from) { for (int i = from; i <= b.Length - pat.Length; i++) { int j = 0; while (j < pat.Length && b[i + j] == pat[j]) j++; if (j == pat.Length) return i; } return -1; }
    }
}

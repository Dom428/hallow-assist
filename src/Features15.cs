// Hallow Assist 1.5:
//  - Hallow Keys for the password of one of Your PCs: runs Keys' own command line (hk.exe --get "<item>" --field password),
//    hidden; the person approves it in Hallow Keys. Only the key made from the password is kept (for this session); the password
//    itself is read as bytes, used once and wiped. It's never logged, stored or shown, and Hallow Assist never writes into Keys.
//  - Copied files (CF_HDROP) between the two PCs, through the normal file transfer (the receiver is asked first).
//  - Session recording on the helper's side (off by default): MP4 with Hallow Clips' FFmpeg if it's there, else MJPEG AVI.
//    It only starts after the PC being helped confirms that its red bar shows "Recording", and stops when that stops.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace HallowAssist
{
    // ---------------------------------------------------------------- Hallow Keys
    public class HkResult
    {
        public int Code; public string Message; public byte[] Key;   // Key: the PBKDF2 key for the session (never the password)
        public bool Ok { get { return Key != null; } }
    }

    public static class HallowKeys
    {
        // hk.exe's exit codes (Hallow Keys 2.6+), plus two of ours
        public const int Ok = 0, Usage = 1, NotRunning = 2, Locked = 3, NotFound = 4, Denied = 5, Off = 6, Other = 7, Missing = -1, TimedOut = -2, Empty = -3, BadItem = -4;
        public const int MaxItem = 200;

        // tests: HALLOW_ASSIST_HK = a stand-in hk.exe (the real one is never used in test mode)
        public static string HkPath()
        {
            if (AssistSettings.TestDir != null)
            {
                var t = Environment.GetEnvironmentVariable("HALLOW_ASSIST_HK");
                return string.IsNullOrWhiteSpace(t) ? Path.Combine(AssistSettings.TestDir, "no-hallow-keys", "hk.exe") : t;
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Hallow Keys", "hk.exe");
        }
        // Keys gives the person 60 s to approve; a little longer here, then hk.exe is stopped
        public static int TimeoutMs
        {
            get
            {
                int s; var t = Environment.GetEnvironmentVariable("HALLOW_ASSIST_HK_TIMEOUT");
                if (AssistSettings.TestDir != null && int.TryParse(t, out s) && s > 0) return s * 1000;
                return 90000;
            }
        }
        // the item's name as typed in Your PCs: one line, not a switch, not too long
        public static string CheckItem(string item)
        {
            var s = (item ?? "").Trim();
            if (s.Length == 0) return "Type the name of the item in Hallow Keys.";
            if (s.Length > MaxItem) return "That name is too long (at most " + MaxItem + " characters).";
            if (s.Any(c => char.IsControl(c))) return "The name can't contain line breaks or tabs.";
            if (s.StartsWith("-")) return "The name can't start with \"-\".";
            return null;
        }
        public static string Message(int code, string item)
        {
            var q = "“" + (item ?? "") + "”";
            switch (code)
            {
                case Ok: return "Got the password from Hallow Keys.";
                case Usage: return "Hallow Keys didn't understand the request - check the item name in Your PCs (Password...).";
                case NotRunning: return "Start Hallow Keys and try again (it isn't running, or it's older than 2.6).";
                case Locked: return "Unlock Hallow Keys and try again.";
                case NotFound: return "Hallow Keys has no item called " + q + " with a password (or several items have that name). Check the name in Your PCs (Password...).";
                case Denied: return "Hallow Keys said no (the request was denied, or nobody answered within 60 seconds).";
                case Off: return "Turn on the command line in Keys > Settings > Developer tools, then try again.";
                case Missing: return "Hallow Keys 2.6 or newer isn't installed on this PC (its command line, hk.exe, wasn't found).";
                case TimedOut: return "Hallow Keys didn't answer in time - try again.";
                case Empty: return "The item " + q + " in Hallow Keys has an empty password.";
                case BadItem: return CheckItem(item) ?? "That item name can't be used.";
                default: return "Hallow Keys couldn't give the password (error " + code + ").";
            }
        }
        // Windows command-line quoting for one argument (what CommandLineToArgvW / the C runtime read back)
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\""); int bs = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { bs++; continue; }
                if (ch == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; continue; }
                sb.Append('\\', bs); bs = 0; sb.Append(ch);
            }
            sb.Append('\\', bs * 2); sb.Append('"');
            return sb.ToString();
        }

        // runs hk.exe hidden, reads the password from its output as bytes and turns it straight into the session key for this
        // code. The bytes and characters are wiped as soon as the key is made; nothing is logged.
        public static HkResult GetKey(string hk, string item, string code, int timeoutMs)
        {
            var bad = CheckItem(item);
            if (bad != null) return new HkResult { Code = BadItem, Message = bad };
            item = item.Trim();
            if (string.IsNullOrEmpty(hk) || !File.Exists(hk)) return new HkResult { Code = Missing, Message = Message(Missing, item) };
            var psi = new ProcessStartInfo(hk, "--get " + Quote(item) + " --field password")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(hk)
            };
            byte[] buf = new byte[256]; int len = 0; bool tooLong = false;
            Process p;
            try { p = Process.Start(psi); }
            catch (Exception e) { return new HkResult { Code = Other, Message = "Couldn't start Hallow Keys' command line (" + e.Message.TrimEnd('.') + ")." }; }
            using (p)
            {
                try { p.StandardInput.Close(); } catch { }
                string err = null;
                var errThread = new Thread(() => { try { var e = p.StandardError.ReadToEnd(); err = e.Length > 300 ? e.Substring(0, 300) : e; } catch { } }) { IsBackground = true };
                var outThread = new Thread(() =>
                {
                    try
                    {
                        var s = p.StandardOutput.BaseStream; var one = new byte[512]; int n;
                        while ((n = s.Read(one, 0, one.Length)) > 0)
                        {
                            if (len + n > 64 * 1024) { tooLong = true; continue; }
                            if (len + n > buf.Length) { var nb = new byte[Math.Max(buf.Length * 2, len + n)]; Buffer.BlockCopy(buf, 0, nb, 0, len); Array.Clear(buf, 0, buf.Length); buf = nb; }
                            Buffer.BlockCopy(one, 0, buf, len, n); len += n;
                        }
                        Array.Clear(one, 0, one.Length);
                    }
                    catch { }
                }) { IsBackground = true };
                errThread.Start(); outThread.Start();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    outThread.Join(2000); Array.Clear(buf, 0, buf.Length);
                    return new HkResult { Code = TimedOut, Message = Message(TimedOut, item) };
                }
                outThread.Join(3000); errThread.Join(1000);
                int code2 = p.ExitCode;
                try
                {
                    if (code2 != Ok)
                    {
                        var m = Message(code2, item);
                        // hk's own words help for "other error" (they never contain a secret: only the reason)
                        if ((code2 == Other || code2 == Usage) && !string.IsNullOrWhiteSpace(err)) m += " Hallow Keys said: " + Family.OneLine(err).Replace("hk: ", "");
                        return new HkResult { Code = code2, Message = m };
                    }
                    if (tooLong) return new HkResult { Code = Other, Message = "Hallow Keys gave back something far too long to be a password." };
                    var chars = new UTF8Encoding(false).GetChars(buf, 0, len);
                    try
                    {
                        int s = 0, e = chars.Length; while (s < e && char.IsWhiteSpace(chars[s])) s++; while (e > s && char.IsWhiteSpace(chars[e - 1])) e--;
                        if (e == s) return new HkResult { Code = Empty, Message = Message(Empty, item) };
                        return new HkResult { Code = Ok, Message = Message(Ok, item), Key = Kdf.PasswordKey(chars, chars.Length, code) };
                    }
                    finally { Array.Clear(chars, 0, chars.Length); }
                }
                finally { Array.Clear(buf, 0, buf.Length); len = 0; }
            }
        }

        public const string SetupText =
            "How to set this up:\n" +
            "1. In Hallow Keys (2.6 or newer), keep this PC's unattended password in an item - for example “Office PC (Assist)” - with the password in its Password field.\n" +
            "2. In Hallow Keys, open Settings > Developer tools and turn on the command line.\n" +
            "3. Type the item's name above, exactly as it is in Keys.\n" +
            "When you click Connect, Hallow Keys asks you to approve it (the prompt names hk.exe from HallowAssist.exe) and only answers while it's unlocked. " +
            "Hallow Assist uses the password once to connect and wipes it: it's never stored, logged or shown here. Hallow Assist never changes anything in Hallow Keys.";
    }

    // "Password for <PC>": type it each time, a remembered key, or from Hallow Keys (the item's name)
    public class PcPasswordDialog : Window
    {
        public string Mode, Item;   // Mode: "type", "remember" (keep the remembered key), "keys"
        readonly RadioButton type, remember, keys; readonly TextBox item; readonly TextBlock err;
        public PcPasswordDialog(SavedPc pc)
        {
            Ui.Dress(this, "Hallow Assist - password for " + pc.Name, 520);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.T("Password for " + pc.Name, 17, true, null, 6));
            sp.Children.Add(Ui.Muted("How Hallow Assist gets the unattended password when you click Connect.", 12.5, 10));
            type = Radio("Type it each time", pc.KeysItem == null && pc.Key == null);
            sp.Children.Add(type);
            remember = Radio("Remembered on this PC (an encrypted key made from it)", pc.KeysItem == null && pc.Key != null);
            if (pc.Key != null) sp.Children.Add(remember);
            keys = Radio("Password from Hallow Keys - the item's name:", pc.KeysItem != null);
            sp.Children.Add(keys);
            item = Ui.Field(pc.KeysItem ?? ""); item.MaxLength = HallowKeys.MaxItem; item.Margin = new Thickness(26, 2, 0, 8);
            item.GotFocus += (s, e) => keys.IsChecked = true;
            sp.Children.Add(item);
            var how = new Border { Background = Ui.R("PanelHi"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 4, 0, 10) };
            how.Child = Ui.Muted(HallowKeys.SetupText, 11.5); ((TextBlock)how.Child).TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(how);
            sp.Children.Add(Ui.Muted("Choosing Hallow Keys forgets a remembered key for this PC: one place keeps the password.", 11.5, 8));
            err = Ui.T("", 12, false, Ui.Amber, 8); sp.Children.Add(err);
            var cancel = Ui.Btn("Cancel", () => { DialogResult = false; }); cancel.IsCancel = true;
            var ok = Ui.Btn("Save", () =>
            {
                if (keys.IsChecked == true) { var e = HallowKeys.CheckItem(item.Text); if (e != null) { err.Text = e; return; } Mode = "keys"; Item = item.Text.Trim(); }
                else if (remember.IsChecked == true) Mode = "remember";
                else Mode = "type";
                DialogResult = true;
            }, true);
            ok.IsDefault = true; ok.Margin = new Thickness(0);
            var row = Ui.Row(cancel, ok); row.HorizontalAlignment = HorizontalAlignment.Right; sp.Children.Add(row);
            Content = sp;
        }
        static RadioButton Radio(string text, bool on) { return new RadioButton { Content = text, IsChecked = on, GroupName = "pw", Foreground = Ui.R("Text"), Margin = new Thickness(0, 3, 0, 3) }; }
        public void SnapFill(string name) { keys.IsChecked = true; item.Text = name; }
    }

    // ---------------------------------------------------------------- copied files (CF_HDROP)
    public interface IFileClip { string[] GetFiles(); void SetFiles(string[] paths); }

    // ---------------------------------------------------------------- session recording (the helper's side)
    public class RecFrame { public byte[] Pixels; public int W, H; public long Version; }

    public class SessionRecorder
    {
        public const int Fps = 10, AudioRate = 16000;
        public const long MaxAvi = 1900L * 1024 * 1024;   // AVI 1.0 stops just under 2 GB
        public static string DefaultFolder()
        {
            if (AssistSettings.TestDir != null) return Path.Combine(AssistSettings.TestDir, "Videos");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hallow Assist");
        }
        // Hallow Clips' first-use copy of FFmpeg (%LOCALAPPDATA%\HallowClips\ffmpeg\ffmpeg.exe) - nothing is downloaded here.
        // Tests: only HALLOW_ASSIST_FFMPEG (unset = the AVI path).
        public static string FindFfmpeg()
        {
            if (AssistSettings.TestDir != null) { var t = Environment.GetEnvironmentVariable("HALLOW_ASSIST_FFMPEG"); return !string.IsNullOrWhiteSpace(t) && File.Exists(t) ? t : null; }
            var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HallowClips", "ffmpeg", "ffmpeg.exe");
            return File.Exists(own) ? own : null;
        }

        readonly Func<long, RecFrame> grab; readonly string folder, label, ffmpeg;
        public string FinalPath, Kind, Error; public long FramesWritten, FramesNew, AudioSamples; public DateTime FirstFrameAt;
        public volatile bool Started;   // the first frame is written
        public bool Running { get { return running; } }
        Thread loop; volatile bool running; readonly object io = new object();
        Stopwatch clock; TimeSpan t0; int tw, th; byte[] scaled; bool hadAudio;
        AviWriter avi; Process ff; Stream ffIn; string tmpVideo, tmpWav; WavWriter wav; StringBuilder ffErr = new StringBuilder();
        static readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

        public SessionRecorder(string folder, string label, string ffmpeg, Func<long, RecFrame> grab) { this.folder = folder; this.label = label; this.ffmpeg = ffmpeg; this.grab = grab; }

        public string Start()
        {
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { return "Couldn't make the recordings folder (" + e.Message.TrimEnd('.') + ")."; }
            running = true; clock = Stopwatch.StartNew();
            loop = new Thread(Loop) { IsBackground = true, Name = "recorder", Priority = ThreadPriority.BelowNormal }; loop.Start();
            return null;
        }
        public static string SafeLabel(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var n = Transfers.SafeName(s); n = new string(n.Where(c => c != '.').ToArray()).Trim();
            return n.Length > 40 ? n.Substring(0, 40) : n;
        }
        string BaseName() { var l = SafeLabel(label); return "Hallow Assist " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + (l.Length > 0 ? " " + l : ""); }

        void Loop()
        {
            long ver = -1, n = 0; RecFrame cur = null; bool fresh = false;
            try
            {
                while (running)
                {
                    var f = grab(ver);
                    if (f != null && f.W > 0 && f.H > 0) { cur = f; ver = f.Version; fresh = true; }
                    if (cur == null) { Thread.Sleep(40); continue; }   // no picture yet
                    if (!Started) { Open(cur.W, cur.H); t0 = clock.Elapsed; FirstFrameAt = DateTime.UtcNow; }
                    long due = (long)((clock.Elapsed - t0).TotalMilliseconds * Fps / 1000.0);
                    while (n <= due && running) { WriteFrame(cur, fresh); fresh = false; n++; Started = true; }
                    if (avi != null && avi.Length > MaxAvi) { Error = "The recording reached 2 GB, so it stopped."; break; }
                    var next = t0 + TimeSpan.FromMilliseconds(n * 1000.0 / Fps);
                    var wait = next - clock.Elapsed; if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                }
            }
            catch (Exception e) { Error = "Recording stopped: " + e.Message.TrimEnd('.') + "."; }
            running = false;
        }
        void Open(int w, int h)
        {
            tw = Math.Max(2, w & ~1); th = Math.Max(2, h & ~1); scaled = new byte[tw * th * 4];
            var b = BaseName();
            if (ffmpeg != null)
            {
                try
                {
                    FinalPath = Unique(Path.Combine(folder, b + ".mp4"));
                    tmpVideo = Path.Combine(folder, Path.GetFileNameWithoutExtension(FinalPath) + ".video.part.mp4");
                    tmpWav = Path.Combine(folder, Path.GetFileNameWithoutExtension(FinalPath) + ".sound.part.wav");
                    ff = Ffmpeg("-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgra -s " + tw + "x" + th + " -framerate " + Fps + " -i - " +
                        VideoCodec(ffmpeg) + " -pix_fmt yuv420p -movflags +faststart \"" + tmpVideo + "\"", true);
                    ffIn = ff.StandardInput.BaseStream;
                    Kind = "mp4"; return;
                }
                catch (Exception e) { KillFf(); TryDelete(tmpVideo); ffErr.Append(e.Message); }
            }
            OpenAvi(b);
        }
        void OpenAvi(string b)
        {
            FinalPath = Unique(Path.Combine(folder, b + ".avi"));
            avi = new AviWriter(FinalPath, tw, th, Fps, AudioRate); Kind = "avi";
        }
        // a software encoder only (the GPU is left alone - the person may be gaming): H.264 if this FFmpeg has one, else MPEG-4 part 2
        static readonly Dictionary<string, string> codecs = new Dictionary<string, string>();
        public static string VideoCodec(string ffmpeg)
        {
            lock (codecs)
            {
                string c; if (codecs.TryGetValue(ffmpeg, out c)) return c;
                var list = "";
                try
                {
                    using (var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                    {
                        var err = p.StandardError.ReadToEndAsync(); list = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
                    }
                }
                catch { }
                Func<string, bool> has = n => System.Text.RegularExpressions.Regex.IsMatch(list, @"^\s*V\S*\s+" + n + @"\s", System.Text.RegularExpressions.RegexOptions.Multiline);
                c = has("libx264") ? "-c:v libx264 -preset veryfast -crf 26" : has("libopenh264") ? "-c:v libopenh264 -b:v 2500k" : "-c:v mpeg4 -q:v 4";
                codecs[ffmpeg] = c; return c;
            }
        }
        Process Ffmpeg(string args, bool stdin)
        {
            var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardInput = stdin, RedirectStandardError = true, RedirectStandardOutput = true };
            var p = Process.Start(psi);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (ffErr) { if (ffErr.Length < 2000) ffErr.AppendLine(e.Data); } };
            p.OutputDataReceived += (s, e) => { };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            return p;
        }
        void WriteFrame(RecFrame f, bool fresh)
        {
            if (fresh || FramesWritten == 0) { Scale(f); FramesNew++; }
            lock (io)
            {
                if (ffIn != null)
                {
                    try { ffIn.Write(scaled, 0, scaled.Length); }
                    catch (IOException)
                    {
                        // FFmpeg couldn't take it (for example a copy without H.264): fall back to the AVI from the start
                        if (FramesWritten < Fps * 2) { KillFf(); TryDelete(tmpVideo); TryDelete(tmpWav); OpenAvi(BaseName()); avi.WriteVideo(Jpeg()); FramesWritten++; return; }
                        throw;
                    }
                }
                else avi.WriteVideo(fresh || FramesWritten == 0 ? Jpeg() : null);   // an empty chunk repeats the last picture
                FramesWritten++;
            }
        }
        unsafe void Scale(RecFrame f)
        {
            fixed (byte* src = f.Pixels) fixed (byte* dst = scaled)
            {
                uint* s = (uint*)src; uint* d = (uint*)dst;
                if (f.W == tw && f.H >= th) { for (int y = 0; y < th; y++) for (int x = 0; x < tw; x++) d[y * tw + x] = s[y * f.W + x] | 0xFF000000; return; }
                for (int y = 0; y < th; y++)
                {
                    int sy = (int)((long)y * f.H / th); var row = s + sy * f.W; var o = d + y * tw;
                    for (int x = 0; x < tw; x++) o[x] = row[(int)((long)x * f.W / tw)] | 0xFF000000;
                }
            }
        }
        byte[] Jpeg()
        {
            var h = GCHandle.Alloc(scaled, GCHandleType.Pinned);
            try
            {
                using (var bmp = new Bitmap(tw, th, tw * 4, PixelFormat.Format32bppRgb, h.AddrOfPinnedObject()))
                using (var ms = new MemoryStream())
                {
                    var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 72L);
                    bmp.Save(ms, jpeg, ep); return ms.ToArray();
                }
            }
            finally { h.Free(); }
        }

        // their PC's sound (16 kHz mono), placed on the recording's timeline: gaps become silence
        public void AddAudio(short[] pcm)
        {
            if (!Started || !running || pcm == null || pcm.Length == 0) return;
            lock (io)
            {
                long expected = (long)((clock.Elapsed - t0).TotalSeconds * AudioRate);
                if (expected - AudioSamples > AudioRate / 5) WriteSamples(new short[expected - AudioSamples - AudioRate / 20]);
                WriteSamples(pcm);
            }
        }
        void WriteSamples(short[] s)
        {
            if (s.Length == 0) return;
            if (avi != null) { if (avi.Length < MaxAvi) avi.WriteAudio(s); }
            else if (ff != null) { if (wav == null) wav = new WavWriter(tmpWav, AudioRate); wav.Write(s); }
            else return;
            hadAudio = true; AudioSamples += s.Length;
        }

        // stops and finishes the file; returns its path (null if nothing was recorded)
        public string Stop()
        {
            running = false;
            var l = loop; if (l != null && l != Thread.CurrentThread) l.Join(5000);
            lock (io)
            {
                if (avi != null) { avi.Close(); avi = null; return FramesWritten > 0 ? FinalPath : null; }
                if (ff == null) return null;
                try { ffIn.Close(); } catch { }
                ffIn = null;
                if (!ff.WaitForExit(60000)) { KillFf(); Error = "FFmpeg didn't finish the recording in time."; }
                var ok = ff.HasExited && ff.ExitCode == 0 && File.Exists(tmpVideo);
                ff.Dispose(); ff = null;
                if (wav != null) { wav.Close(); wav = null; }
                if (!ok) { if (Error == null) Error = "FFmpeg couldn't write the recording" + (ffErr.Length > 0 ? " (" + Family.OneLine(ffErr.ToString()) + ")" : "") + "."; TryDelete(tmpWav); return File.Exists(tmpVideo) ? Keep(tmpVideo) : null; }
                if (hadAudio && File.Exists(tmpWav))
                {
                    try
                    {
                        using (var mux = Ffmpeg("-hide_banner -loglevel error -y -i \"" + tmpVideo + "\" -i \"" + tmpWav + "\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 64k -movflags +faststart \"" + FinalPath + "\"", false))
                        {
                            if (mux.WaitForExit(120000) && mux.ExitCode == 0 && File.Exists(FinalPath)) { TryDelete(tmpVideo); TryDelete(tmpWav); return FinalPath; }
                            try { if (!mux.HasExited) mux.Kill(); } catch { }
                        }
                    }
                    catch { }
                    TryDelete(FinalPath); Error = "The sound couldn't be added, so the recording has the picture only.";
                }
                TryDelete(tmpWav);
                return Keep(tmpVideo);
            }
        }
        string Keep(string tmp) { try { File.Move(tmp, FinalPath); return FinalPath; } catch { return tmp; } }
        void KillFf() { try { if (ff != null && !ff.HasExited) ff.Kill(); } catch { } try { if (ff != null) ff.Dispose(); } catch { } ff = null; ffIn = null; }
        static void TryDelete(string p) { try { if (p != null && File.Exists(p)) File.Delete(p); } catch { } }
        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path); var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
            for (int i = 2; ; i++) { var p = Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(p)) return p; }
        }
        public static string Elapsed(TimeSpan t) { return t.TotalHours >= 1 ? ((int)t.TotalHours) + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00") : t.Minutes + ":" + t.Seconds.ToString("00"); }
    }

    // AVI 1.0 with an MJPEG video stream and (when their sound arrived) a 16 kHz mono PCM stream. The headers are written at
    // the end into space kept free at the start; an empty video chunk means "same picture as before".
    public class AviWriter
    {
        const int HeaderSpace = 4096;
        readonly FileStream fs; readonly BinaryWriter w; readonly int width, height, fps, rate;
        readonly long moviFourcc; readonly List<uint[]> index = new List<uint[]>();   // fourcc, flags, offset, size
        int frames, maxChunk; long samples; bool closed;
        public long Length { get { return fs.Length; } }
        public int Frames { get { return frames; } }
        public AviWriter(string path, int width, int height, int fps, int rate)
        {
            this.width = width; this.height = height; this.fps = fps; this.rate = rate;
            fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite); w = new BinaryWriter(fs);
            Cc("RIFF"); w.Write(0); Cc("AVI ");
            Cc("JUNK"); w.Write(HeaderSpace - 8); w.Write(new byte[HeaderSpace - 8]);
            Cc("LIST"); w.Write(0); moviFourcc = fs.Position; Cc("movi");
        }
        void Cc(string s) { w.Write(Encoding.ASCII.GetBytes(s)); }
        void Chunk(string id, byte[] data, int len, uint flags)
        {
            index.Add(new[] { Fourcc(id), flags, (uint)(fs.Position - moviFourcc), (uint)len });
            Cc(id); w.Write(len); if (len > 0) w.Write(data, 0, len); if ((len & 1) == 1) w.Write((byte)0);
            maxChunk = Math.Max(maxChunk, len);
        }
        static uint Fourcc(string s) { var b = Encoding.ASCII.GetBytes(s); return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24); }
        public void WriteVideo(byte[] jpeg) { Chunk("00dc", jpeg ?? new byte[0], jpeg == null ? 0 : jpeg.Length, jpeg == null ? 0u : 0x10u); frames++; }
        public void WriteAudio(short[] pcm)
        {
            var b = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, b, 0, b.Length);
            Chunk("01wb", b, b.Length, 0x10); samples += pcm.Length;
        }
        public void Close()
        {
            if (closed) return; closed = true;
            long moviEnd = fs.Position;
            Cc("idx1"); w.Write(index.Count * 16); foreach (var e in index) { w.Write(e[0]); w.Write(e[1]); w.Write(e[2]); w.Write(e[3]); }
            long end = fs.Position;
            fs.Position = moviFourcc - 4; w.Write((uint)(moviEnd - moviFourcc));
            fs.Position = 4; w.Write((uint)(end - 8));
            // the headers, then JUNK up to the movi list
            bool audio = samples > 0;
            var h = new MemoryStream(); var hw = new BinaryWriter(h);
            Action<string> cc = s => hw.Write(Encoding.ASCII.GetBytes(s));
            int vStrl = 4 + (8 + 56) + (8 + 40), aStrl = 4 + (8 + 56) + (8 + 18);
            int hdrl = 4 + (8 + 56) + (8 + vStrl) + (audio ? 8 + aStrl : 0);
            cc("LIST"); hw.Write(hdrl); cc("hdrl");
            cc("avih"); hw.Write(56);
            hw.Write(1000000 / fps); hw.Write(maxChunk * fps); hw.Write(0); hw.Write(0x10 | 0x100); hw.Write(frames); hw.Write(0); hw.Write(audio ? 2 : 1); hw.Write(maxChunk + 8);
            hw.Write(width); hw.Write(height); hw.Write(0); hw.Write(0); hw.Write(0); hw.Write(0);
            cc("LIST"); hw.Write(vStrl); cc("strl");
            cc("strh"); hw.Write(56); cc("vids"); cc("MJPG"); hw.Write(0); hw.Write((short)0); hw.Write((short)0); hw.Write(0); hw.Write(1); hw.Write(fps); hw.Write(0); hw.Write(frames); hw.Write(maxChunk); hw.Write(-1); hw.Write(0);
            hw.Write((short)0); hw.Write((short)0); hw.Write((short)width); hw.Write((short)height);
            cc("strf"); hw.Write(40); hw.Write(40); hw.Write(width); hw.Write(height); hw.Write((short)1); hw.Write((short)24); cc("MJPG"); hw.Write(width * height * 3); hw.Write(0); hw.Write(0); hw.Write(0); hw.Write(0);
            if (audio)
            {
                cc("LIST"); hw.Write(aStrl); cc("strl");
                cc("strh"); hw.Write(56); cc("auds"); hw.Write(0); hw.Write(0); hw.Write((short)0); hw.Write((short)0); hw.Write(0); hw.Write(2); hw.Write(rate * 2); hw.Write(0); hw.Write((int)samples); hw.Write(rate / 5 * 2); hw.Write(-1); hw.Write(2);
                hw.Write((short)0); hw.Write((short)0); hw.Write((short)0); hw.Write((short)0);
                cc("strf"); hw.Write(18); hw.Write((short)1); hw.Write((short)1); hw.Write(rate); hw.Write(rate * 2); hw.Write((short)2); hw.Write((short)16); hw.Write((short)0);
            }
            hw.Flush();
            var hb = h.ToArray();
            fs.Position = 12; w.Write(hb);
            Cc("JUNK"); w.Write(HeaderSpace - hb.Length - 8);
            w.Flush(); fs.Dispose();
        }
    }

    // a plain 16-bit mono WAV (the sound of an MP4 recording until FFmpeg puts the two together)
    public class WavWriter
    {
        readonly FileStream fs; readonly BinaryWriter w; long bytes;
        public WavWriter(string path, int rate)
        {
            fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite); w = new BinaryWriter(fs);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(0); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(0);
        }
        public void Write(short[] s) { var b = new byte[s.Length * 2]; Buffer.BlockCopy(s, 0, b, 0, b.Length); w.Write(b); bytes += b.Length; }
        public void Close() { fs.Position = 4; w.Write((uint)(36 + bytes)); fs.Position = 40; w.Write((uint)bytes); w.Flush(); fs.Dispose(); }
    }
}

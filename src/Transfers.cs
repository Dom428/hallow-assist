// Hallow Assist file transfer, both ways, inside the sealed channel. The receiving side is always asked first
// (file-offer -> Accept/Decline); files land in Downloads\Hallow Assist (or the folder picked in Settings) with a name that
// never overwrites anything. v1.5: several files (copied files) go as one "file-batch" with one question for all of them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HallowAssist
{
    public class Transfers
    {
        public const long MaxSize = 4L * 1024 * 1024 * 1024;
        public const int MaxBatch = 100;           // v1.5: copied files at once
        readonly Action<byte[]> send; readonly Func<long> queued; readonly Func<bool> alive;
        public string Folder;
        public bool PeerBatches;                   // v1.5: the other app understands "file-batch" (else one offer per file)
        public event Action<string, long, Action<bool>> Offered;            // name, size, answer(accept)
        public event Action<string[], long, bool, Action<bool>> OfferedMany; // v1.5: names, total size, copied files, answer(accept)
        public event Action<string> Note;                                   // one line for the UI
        public event Action<string> Received;                               // full path
        public event Action<string[], bool> BatchReceived;                  // v1.5: the files of a batch that arrived complete, copied files
        class Out { public string Path, Name; public long Size; }
        class In { public string Name, Part; public long Size, Got; public FileStream Stream; public Batch Batch; }
        class Batch { public int Left; public bool Clip; public readonly List<string> Done = new List<string>(); }
        readonly Dictionary<uint, Out> outgoing = new Dictionary<uint, Out>();
        readonly Dictionary<uint, In> incoming = new Dictionary<uint, In>();
        readonly Queue<uint> sendQueue = new Queue<uint>(); bool sending;
        uint nextId = 1;
        public int OffersSent; public long BytesSent;   // v1.6 tests: what left this side (offers, file bytes)

        public Transfers(Action<byte[]> send, Func<long> queued, Func<bool> alive, string folder) { this.send = send; this.queued = queued; this.alive = alive; Folder = folder; }
        void Say(string s) { var h = Note; if (h != null) h(s); }
        public static string Size(long n) { return n < 1024 ? n + " B" : n < 1048576 ? (n / 1024) + " KB" : n < 1073741824 ? (n / 1048576.0).ToString("0.0") + " MB" : (n / 1073741824.0).ToString("0.00") + " GB"; }
        public static string SafeName(string n)
        {
            n = (n ?? "").Replace('\\', '/'); n = n.Substring(n.LastIndexOf('/') + 1);
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimEnd('.'); if (n.Length == 0) n = "file"; if (n.Length > 120) n = n.Substring(0, 120);
            // v1.1: every reserved device name, whatever follows the first dot ("CON.tar.gz", "com7 .txt" too)
            var dot = n.IndexOf('.'); var stem = (dot < 0 ? n : n.Substring(0, dot)).Trim().ToUpperInvariant();
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "CONIN$" || stem == "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) n = "_" + n;
            return n;
        }
        // v1.1: programs, scripts and shortcuts - the prompt says so, because opening one runs it
        static readonly string[] RiskyExt = { ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".reg", ".jar", ".dll", ".sys", ".inf", ".iso", ".img", ".vhd", ".vhdx", ".application", ".appref-ms", ".settingcontent-ms", ".library-ms", ".search-ms", ".docm", ".xlsm", ".pptm" };
        public static bool Risky(string name) { var e = System.IO.Path.GetExtension(name ?? "").ToLowerInvariant(); return RiskyExt.Contains(e); }
        public static string Warning(string name) { return Risky(name) ? " It's a program, script or shortcut: opening it runs it, so only open it if you trust them." : ""; }
        // v1.5: the question for several files at once
        public static string BatchText(string[] names, long total, string folder, bool clip)
        {
            var shown = names.Take(3).ToList();
            var list = names.Length <= 3 ? string.Join(", ", shown) : string.Join(", ", shown) + " and " + (names.Length - 3) + " more";
            var risky = names.Where(Risky).Take(3).ToList();
            return list + " (" + Size(total) + (names.Length > 1 ? " in all" : "") + "). " + (names.Length == 1 ? "It goes" : "They go") + " into " + folder + "." +
                (clip ? " Then " + (names.Length == 1 ? "it's" : "they're") + " on your clipboard too: paste with Ctrl+V into any folder." : "") +
                " Only accept files you expect." +
                (risky.Count > 0 ? " " + (names.Length == 1 ? "It's" : "Some are") + " programs, scripts or shortcuts (" + string.Join(", ", risky) + "): opening one runs it, so only open them if you trust them." : "");
        }
        public static string BatchTitle(string who, int n, bool clip) { return who + (who == "They" ? " want" : " wants") + " to send you " + (n == 1 ? "a file" : n + " files") + (clip ? " they copied" : ""); }

        // v1.1: mark received files as coming from the internet (Mark of the Web), so Windows SmartScreen and Office
        // Protected View treat them like a download instead of a file you made yourself
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);
        public static bool MarkFromInternet(string path)
        {
            try
            {
                using (var h = CreateFileW(path + ":Zone.Identifier", 0x40000000 /* GENERIC_WRITE */, 0, IntPtr.Zero, 2 /* CREATE_ALWAYS */, 0x80, IntPtr.Zero))
                {
                    if (h.IsInvalid) return false;
                    using (var fs = new FileStream(h, FileAccess.Write)) { var b = System.Text.Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"); fs.Write(b, 0, b.Length); }
                }
                return true;
            }
            catch { return false; }
        }

        public string Offer(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "That file isn't there any more.";
            if (fi.Length > MaxSize) return "That file is over 4 GB.";
            uint id; lock (outgoing) { id = nextId++; outgoing[id] = new Out { Path = path, Name = fi.Name, Size = fi.Length }; }
            send(Msg.JsonMsg("t", "file-offer", "id", id, "name", fi.Name, "size", fi.Length)); OffersSent++;
            Say("Offered " + fi.Name + " (" + Size(fi.Length) + ") - waiting for them to accept.");
            return null;
        }

        // v1.5: several files at once (copied files): folders are skipped, at most MaxBatch files and 4 GB in all.
        // Returns an error for the UI, or null when they were offered.
        public string OfferMany(string[] paths, bool clip)
        {
            List<FileInfo> files; string skipped;
            var err = Pick(paths, out files, out skipped); if (err != null) return err;
            long total = files.Sum(f => f.Length);
            if (!PeerBatches)
            {
                // an older app on the other side: one question per file (and nothing lands on its clipboard)
                foreach (var f in files) { var e = Offer(f.FullName); if (e != null) return e; }
                if (skipped.Length > 0) Say(skipped.Trim());
                return null;
            }
            var list = new List<Dictionary<string, object>>();
            lock (outgoing)
                foreach (var f in files)
                {
                    var id = nextId++; outgoing[id] = new Out { Path = f.FullName, Name = f.Name, Size = f.Length };
                    list.Add(new Dictionary<string, object> { { "id", id }, { "name", f.Name }, { "size", f.Length } });
                }
            send(Msg.JsonMsg("t", "file-batch", "clip", clip, "files", list.ToArray())); OffersSent++;
            Say("Offered " + (files.Count == 1 ? files[0].Name : files.Count + " files") + " (" + Size(total) + ") - waiting for them to accept." + skipped);
            return null;
        }

        // v1.6 (from OfferMany): the files among copied paths, with the same limits; an error for the UI, or null
        public static string Pick(string[] paths, out List<FileInfo> files, out string skipped)
        {
            files = new List<FileInfo>(); int folders = 0, missing = 0;
            foreach (var p in paths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try { if (Directory.Exists(p)) { folders++; continue; } var fi = new FileInfo(p); if (fi.Exists) files.Add(fi); else missing++; } catch { missing++; }
            }
            skipped = (folders > 0 ? " Folders aren't sent - zip them first." : "") + (missing > 0 ? " " + missing + " copied " + (missing == 1 ? "file isn't" : "files aren't") + " there any more." : "");
            if (files.Count == 0) return (folders > 0 || missing > 0 ? "There are no files to send." : "Nothing is copied - copy files in Explorer first.") + skipped;
            if (files.Count > MaxBatch) return "That's " + files.Count + " files - at most " + MaxBatch + " at once (zip them first).";
            var big = files.FirstOrDefault(f => f.Length > MaxSize); if (big != null) return big.Name + " is over 4 GB.";
            if (files.Sum(f => f.Length) > MaxSize) return "Those files are over 4 GB together - send fewer at once.";
            return null;
        }

        // JSON messages about files; true if it was one
        public bool Handle(Dictionary<string, object> m)
        {
            var t = Msg.Str(m, "t"); uint id = (uint)Msg.Num(m, "id");
            switch (t)
            {
                case "file-offer":
                    {
                        var name = SafeName(Msg.Str(m, "name")); long size = Msg.Num(m, "size");
                        if (size < 0 || size > MaxSize) { send(Msg.JsonMsg("t", "file-decline", "id", id)); Say("Declined " + name + ": too big."); return true; }
                        var h = Offered;
                        if (h == null) { send(Msg.JsonMsg("t", "file-decline", "id", id)); return true; }
                        int answered = 0;
                        h(name, size, ok =>
                        {
                            if (Interlocked.Exchange(ref answered, 1) == 1 || !alive()) return;
                            if (!ok) { send(Msg.JsonMsg("t", "file-decline", "id", id)); Say("Declined " + name + "."); return; }
                            if (Open(id, name, size, null)) { send(Msg.JsonMsg("t", "file-accept", "id", id)); Say("Receiving " + name + " (" + Size(size) + ")..."); }
                            else send(Msg.JsonMsg("t", "file-decline", "id", id));
                        });
                        return true;
                    }
                case "file-batch": HandleBatch(m); return true;
                case "file-accept":
                    {
                        Out o; lock (outgoing) outgoing.TryGetValue(id, out o);
                        if (o == null) return true;
                        lock (sendQueue)
                        {
                            if (sendQueue.Contains(id)) return true;
                            sendQueue.Enqueue(id);
                            if (!sending) { sending = true; new Thread(SendLoop) { IsBackground = true, Name = "file send" }.Start(); }
                        }
                        return true;
                    }
                case "file-decline": { Out o; lock (outgoing) { outgoing.TryGetValue(id, out o); outgoing.Remove(id); } if (o != null) Say("They declined " + o.Name + "."); return true; }
                case "file-done":
                    {
                        In f; lock (incoming) { incoming.TryGetValue(id, out f); incoming.Remove(id); }
                        if (f == null) return true;
                        f.Stream.Dispose();
                        if (f.Got != f.Size) { TryDelete(f.Part); Say(f.Name + " arrived incomplete and was removed."); BatchStep(f.Batch, null); return true; }
                        var dest = Unique(System.IO.Path.Combine(Folder, f.Name));
                        File.Move(f.Part, dest);
                        MarkFromInternet(dest);
                        Say("Received " + System.IO.Path.GetFileName(dest) + " (" + Size(f.Size) + ") in " + Folder + ".");
                        var h = Received; if (h != null) h(dest);
                        BatchStep(f.Batch, dest);
                        return true;
                    }
                case "file-cancel": { In f; lock (incoming) { incoming.TryGetValue(id, out f); incoming.Remove(id); } if (f != null) { f.Stream.Dispose(); TryDelete(f.Part); Say(f.Name + " was cancelled."); BatchStep(f.Batch, null); } return true; }
            }
            return false;
        }
        bool Open(uint id, string name, long size, Batch b)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var part = System.IO.Path.Combine(Folder, name + "." + id + ".part");
                lock (incoming)
                {
                    if (incoming.ContainsKey(id)) return false;
                    incoming[id] = new In { Name = name, Part = part, Size = size, Batch = b, Stream = new FileStream(part, FileMode.Create, FileAccess.Write) };
                }
                return true;
            }
            catch (Exception e) { Say("Couldn't save " + name + ": " + e.Message); return false; }
        }
        // v1.5: one question for several files; every name and size is checked as for a single file, and the whole batch too
        void HandleBatch(Dictionary<string, object> m)
        {
            var ids = new List<uint>(); var names = new List<string>(); var sizes = new List<long>(); bool bad = false;
            object fo; if (m.TryGetValue("files", out fo) && fo is System.Collections.ArrayList)
                foreach (var x in (System.Collections.ArrayList)fo)
                {
                    var d = x as Dictionary<string, object>; if (d == null) { bad = true; continue; }
                    var id = (uint)Msg.Num(d, "id"); var size = Msg.Num(d, "size");
                    if (id == 0 || ids.Contains(id) || size < 0 || size > MaxSize) bad = true;
                    ids.Add(id); names.Add(SafeName(Msg.Str(d, "name"))); sizes.Add(size);
                }
            bool clip = Msg.Bool(m, "clip"); long total = sizes.Sum();
            Action declineAll = () => { foreach (var i in ids) send(Msg.JsonMsg("t", "file-decline", "id", i)); };
            if (ids.Count == 0) return;
            if (bad || ids.Count > MaxBatch || total > MaxSize) { declineAll(); Say("Declined " + ids.Count + " files: too many or too big."); return; }
            var h = OfferedMany;
            if (h == null) { declineAll(); return; }
            int answered = 0;
            h(names.ToArray(), total, clip, ok =>
            {
                if (Interlocked.Exchange(ref answered, 1) == 1 || !alive()) return;
                if (!ok) { declineAll(); Say("Declined " + (ids.Count == 1 ? names[0] : ids.Count + " files") + "."); return; }
                var b = new Batch { Left = ids.Count, Clip = clip };
                for (int i = 0; i < ids.Count; i++)
                {
                    if (Open(ids[i], names[i], sizes[i], b)) send(Msg.JsonMsg("t", "file-accept", "id", ids[i]));
                    else { send(Msg.JsonMsg("t", "file-decline", "id", ids[i])); BatchStep(b, null); }
                }
                Say("Receiving " + (ids.Count == 1 ? names[0] : ids.Count + " files") + " (" + Size(total) + ")...");
            });
        }
        void BatchStep(Batch b, string done)
        {
            if (b == null) return;
            string[] all = null;
            lock (b) { if (done != null) b.Done.Add(done); b.Left--; if (b.Left == 0) all = b.Done.ToArray(); }
            if (all == null || all.Length == 0) return;
            var h = BatchReceived; if (h != null) h(all, b.Clip);
        }
        public void Chunk(byte[] b)
        {
            if (b.Length < 5) return;
            uint id = Msg.RU32(b, 1); In f; lock (incoming) incoming.TryGetValue(id, out f);
            if (f == null) return;
            f.Got += b.Length - 5;
            if (f.Got > f.Size) { lock (incoming) incoming.Remove(id); f.Stream.Dispose(); TryDelete(f.Part); send(Msg.JsonMsg("t", "file-cancel", "id", id)); Say(f.Name + " sent more than offered - stopped."); BatchStep(f.Batch, null); return; }
            f.Stream.Write(b, 5, b.Length - 5);
        }
        // accepted files go out one after another
        void SendLoop()
        {
            while (true)
            {
                uint id; Out o;
                lock (sendQueue) { if (sendQueue.Count == 0) { sending = false; return; } id = sendQueue.Dequeue(); }
                lock (outgoing) outgoing.TryGetValue(id, out o);
                if (o != null) Stream(id, o);
            }
        }
        void Stream(uint id, Out o)
        {
            try
            {
                using (var fs = File.OpenRead(o.Path))
                {
                    var buf = new byte[64 * 1024]; long sent = 0; int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (!alive()) return;
                        while (queued() > 2 * 1024 * 1024 && alive()) Thread.Sleep(15);
                        send(Msg.FileChunk(id, buf, 0, n)); sent += n; BytesSent += n;
                        if (sent > o.Size) break;
                    }
                    send(Msg.JsonMsg("t", "file-done", "id", id, "size", sent));
                    Say("Sent " + o.Name + ".");
                }
            }
            catch (Exception e) { send(Msg.JsonMsg("t", "file-cancel", "id", id)); Say("Couldn't send " + o.Name + ": " + e.Message); }
            finally { lock (outgoing) outgoing.Remove(id); }
        }
        public void CancelAll()
        {
            lock (incoming) { foreach (var f in incoming.Values) { try { f.Stream.Dispose(); } catch { } TryDelete(f.Part); } incoming.Clear(); }
            lock (outgoing) outgoing.Clear();
            lock (sendQueue) sendQueue.Clear();
        }
        static void TryDelete(string p) { try { File.Delete(p); } catch { } }
        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = System.IO.Path.GetDirectoryName(path); var stem = System.IO.Path.GetFileNameWithoutExtension(path); var ext = System.IO.Path.GetExtension(path);
            for (int i = 2; ; i++) { var p = System.IO.Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(p)) return p; }
        }
        public static string DownloadFolder()
        {
            if (AssistSettings.TestDir != null) return System.IO.Path.Combine(AssistSettings.TestDir, "Downloads");
            var dl = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try { var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"); var v = k == null ? null : k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string; if (!string.IsNullOrEmpty(v)) return System.IO.Path.Combine(v, "Hallow Assist"); } catch { }
            return System.IO.Path.Combine(dl, "Downloads", "Hallow Assist");
        }
    }
}

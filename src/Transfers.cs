// Hallow Assist file transfer, both ways, inside the sealed channel. The receiving side is always asked first
// (file-offer -> Accept/Decline); files land in Downloads\Hallow Assist with a name that never overwrites anything.
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
        readonly Action<byte[]> send; readonly Func<long> queued; readonly Func<bool> alive;
        public string Folder;
        public event Action<string, long, Action<bool>> Offered;   // name, size, answer(accept)
        public event Action<string> Note;                          // one line for the UI
        public event Action<string> Received;                      // full path
        class Out { public string Path, Name; public long Size; }
        class In { public string Name, Part; public long Size, Got; public FileStream Stream; }
        readonly Dictionary<uint, Out> outgoing = new Dictionary<uint, Out>();
        readonly Dictionary<uint, In> incoming = new Dictionary<uint, In>();
        uint nextId = 1;

        public Transfers(Action<byte[]> send, Func<long> queued, Func<bool> alive, string folder) { this.send = send; this.queued = queued; this.alive = alive; Folder = folder; }
        void Say(string s) { var h = Note; if (h != null) h(s); }
        public static string Size(long n) { return n < 1024 ? n + " B" : n < 1048576 ? (n / 1024) + " KB" : n < 1073741824 ? (n / 1048576.0).ToString("0.0") + " MB" : (n / 1073741824.0).ToString("0.00") + " GB"; }
        public static string SafeName(string n)
        {
            n = (n ?? "").Replace('\\', '/'); n = n.Substring(n.LastIndexOf('/') + 1);
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
            n = n.Trim().TrimEnd('.'); if (n.Length == 0) n = "file"; if (n.Length > 120) n = n.Substring(0, 120);
            var stem = System.IO.Path.GetFileNameWithoutExtension(n).ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "LPT1", "LPT2" }.Contains(stem)) n = "_" + n;
            return n;
        }

        public string Offer(string path)
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return "That file isn't there any more.";
            if (fi.Length > MaxSize) return "That file is over 4 GB.";
            uint id; lock (outgoing) { id = nextId++; outgoing[id] = new Out { Path = path, Name = fi.Name, Size = fi.Length }; }
            send(Msg.JsonMsg("t", "file-offer", "id", id, "name", fi.Name, "size", fi.Length));
            Say("Offered " + fi.Name + " (" + Size(fi.Length) + ") - waiting for them to accept.");
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
                            try
                            {
                                Directory.CreateDirectory(Folder);
                                var part = System.IO.Path.Combine(Folder, name + "." + id + ".part");
                                lock (incoming) incoming[id] = new In { Name = name, Part = part, Size = size, Stream = new FileStream(part, FileMode.Create, FileAccess.Write) };
                                send(Msg.JsonMsg("t", "file-accept", "id", id));
                                Say("Receiving " + name + " (" + Size(size) + ")...");
                            }
                            catch (Exception e) { send(Msg.JsonMsg("t", "file-decline", "id", id)); Say("Couldn't save " + name + ": " + e.Message); }
                        });
                        return true;
                    }
                case "file-accept":
                    {
                        Out o; lock (outgoing) outgoing.TryGetValue(id, out o);
                        if (o != null) new Thread(() => Stream(id, o)) { IsBackground = true, Name = "file send" }.Start();
                        return true;
                    }
                case "file-decline": { Out o; lock (outgoing) { outgoing.TryGetValue(id, out o); outgoing.Remove(id); } if (o != null) Say("They declined " + o.Name + "."); return true; }
                case "file-done":
                    {
                        In f; lock (incoming) { incoming.TryGetValue(id, out f); incoming.Remove(id); }
                        if (f == null) return true;
                        f.Stream.Dispose();
                        if (f.Got != f.Size) { TryDelete(f.Part); Say(f.Name + " arrived incomplete and was removed."); return true; }
                        var dest = Unique(System.IO.Path.Combine(Folder, f.Name));
                        File.Move(f.Part, dest);
                        Say("Received " + System.IO.Path.GetFileName(dest) + " (" + Size(f.Size) + ") in " + Folder + ".");
                        var h = Received; if (h != null) h(dest);
                        return true;
                    }
                case "file-cancel": { In f; lock (incoming) { incoming.TryGetValue(id, out f); incoming.Remove(id); } if (f != null) { f.Stream.Dispose(); TryDelete(f.Part); Say(f.Name + " was cancelled."); } return true; }
            }
            return false;
        }
        public void Chunk(byte[] b)
        {
            if (b.Length < 5) return;
            uint id = Msg.RU32(b, 1); In f; lock (incoming) incoming.TryGetValue(id, out f);
            if (f == null) return;
            f.Got += b.Length - 5;
            if (f.Got > f.Size) { lock (incoming) incoming.Remove(id); f.Stream.Dispose(); TryDelete(f.Part); send(Msg.JsonMsg("t", "file-cancel", "id", id)); Say(f.Name + " sent more than offered - stopped."); return; }
            f.Stream.Write(b, 5, b.Length - 5);
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
                        send(Msg.FileChunk(id, buf, 0, n)); sent += n;
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

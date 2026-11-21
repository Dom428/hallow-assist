// Hallow Assist 1.6 unit tests: "Copy their files here" asks the person being helped first. A real HostSession (no relay:
// its JSON handler is called directly, and its file-transfer end is swapped for one that records what would leave the PC)
// gets the helper's request; nothing may be offered or sent before Allow, Deny and the timeout send nothing, a 1.5 helper's
// request is asked about too, and a 1.6 helper refuses to ask a 1.5 PC. Everything goes into a temporary folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static void V16()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "HallowAssistTests16-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try { V16Text(); V16Host(tmp); V16Viewer(); }
            finally { FilePull.TimeoutOverrideMs = 0; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void V16Text()
        {
            var a = new PullAsk { Names = new[] { "invoice.pdf", "photos.zip", "notes.txt" }, Total = 3 * 1048576L };
            Ok(FilePull.Title("Dom", 3) == "Dom wants to copy these 3 files from your PC" && FilePull.Title("Dom", 1) == "Dom wants to copy this file from your PC" && FilePull.Title(null, 2).StartsWith("Your helper"), "file ask: the title names the helper and the number of files");
            var t = FilePull.Text(a);
            Ok(t.StartsWith("invoice.pdf, photos.zip, notes.txt (3.0 MB in all)") && t.Contains("unless you click Allow"), "file ask: the text lists the names and the total size (" + t + ")");
            var many = new PullAsk { Names = Enumerable.Range(1, 9).Select(i => "f" + i + ".txt").ToArray(), Total = 9 };
            Ok(FilePull.List(many).Contains("f6.txt and 3 more") && !FilePull.List(many).Contains("f7.txt"), "file ask: long lists say how many more");
            int calls = 0; PullResult got = PullResult.Cancelled;
            var once = FilePull.Once(r => { calls++; got = r; }, 60000);
            once(PullResult.Deny); once(PullResult.Allow);
            Ok(calls == 1 && got == PullResult.Deny, "file ask: only the first answer counts");
            calls = 0; FilePull.Once(r => { calls++; got = r; }, 150);
            Ok(Wait(() => calls == 1, 3000) && got == PullResult.Timeout, "file ask: no answer -> the timeout answers (as Deny)");
            Ok(FilePull.AnswerText(false, "deny", null) == "They said no." && FilePull.AnswerText(false, "timeout", null).StartsWith("They said no") && FilePull.AnswerText(true, "allow", null).StartsWith("They allowed it") && FilePull.AnswerText(false, "clipboard", "X") == "X", "file ask: the helper's lines for allow / deny / timeout / can't");
        }

        class HostRig
        {
            public HostSession H; public readonly List<string> Sent = new List<string>(); public readonly List<string[]> Batches = new List<string[]>();
            public int Asked, Closed; public string Title, Text; public Action<bool> Answer;
            public void Json(params object[] kv)
            {
                var d = new Dictionary<string, object>(); for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
                typeof(HostSession).GetMethod("OnJson", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(H, new object[] { d });
            }
            public int Offers { get { lock (Sent) return Sent.Count(s => s == "file-batch" || s == "file-offer"); } }
        }
        static HostRig NewHost(string dir, string[] copied, HostOptions opts = null)   // v1.7: opts (e.g. Unattended)
        {
            Directory.CreateDirectory(dir);
            var clip = Path.Combine(dir, "clipboard.txt"); File.WriteAllLines(Path.Combine(dir, "clipboard-files.txt"), copied);
            var r = new HostRig();
            opts = opts ?? new HostOptions(); opts.Screen = new SyntheticSource(); opts.FileClip = new FileClip(clip);
            r.H = new HostSession(opts) { State = HostState.Connected, HelperName = "Dom" };
            r.H.Clipboard = true;
            var tr = new Transfers(m =>
            {
                if (m[0] != Msg.Json) return;
                var d = Msg.ParseJson(m); var t = Msg.Str(d, "t"); lock (r.Sent) r.Sent.Add(t);
                object fo; if (t == "file-batch" && d.TryGetValue("files", out fo)) lock (r.Batches) r.Batches.Add(((System.Collections.ArrayList)fo).Cast<Dictionary<string, object>>().Select(x => Msg.Str(x, "name")).ToArray());
            }, () => 0, () => true, Path.Combine(dir, "dl")) { PeerBatches = true };
            r.H.Files = tr;
            r.H.FilesAskNeeded += (title, text, answer) => { r.Asked++; r.Title = title; r.Text = text; r.Answer = answer; };
            r.H.FilesAskClosed += () => r.Closed++;
            return r;
        }

        static void V16Host(string tmp)
        {
            FilePull.TimeoutOverrideMs = 60000;
            var src = Path.Combine(tmp, "src"); Directory.CreateDirectory(Path.Combine(src, "a folder"));
            var f1 = Path.Combine(src, "secret-notes.txt"); File.WriteAllText(f1, "only after Allow");
            var f2 = Path.Combine(src, "photo.bin"); File.WriteAllBytes(f2, Enumerable.Range(0, 200000).Select(i => (byte)(i % 249)).ToArray());
            var copied = new[] { f1, f2, Path.Combine(src, "a folder") };

            // Allow
            var r = NewHost(Path.Combine(tmp, "allow"), copied);
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 1 && r.Title == "Dom wants to copy these 2 files from your PC" && r.Text.Contains("secret-notes.txt, photo.bin (") && r.Text.Contains("Folders aren't sent"), "file ask: the PC is asked first, with the helper's name, the files and their size");
            Thread.Sleep(300);
            Ok(r.H.PullPending && r.Offers == 0 && r.H.Files.BytesSent == 0, "file ask: before Allow nothing leaves the PC - no offer, no file bytes");
            r.Answer(true);
            Ok(r.Offers == 1 && r.Batches.Count == 1 && r.Batches[0].SequenceEqual(new[] { "secret-notes.txt", "photo.bin" }) && r.H.PullsAllowed == 1 && !r.H.PullPending && r.Closed == 1, "file ask: Allow -> exactly the listed files are offered (one batch)");
            Ok(r.H.Files.BytesSent == 0, "file ask: ...and still no bytes until the helper's side accepts");
            foreach (var id in new[] { 1, 2 }) r.H.Files.Handle(new Dictionary<string, object> { { "t", "file-accept" }, { "id", id } });
            Ok(Wait(() => r.H.Files.BytesSent == 200000 + "only after Allow".Length), "file ask: once both sides said yes, the files go out (" + r.H.Files.BytesSent + " bytes)");
            r.Answer(true);
            Ok(r.Offers == 1, "file ask: a second click on the same question does nothing");

            // Deny
            r = NewHost(Path.Combine(tmp, "deny"), copied);
            r.Json("t", "clip-files-ask"); r.Answer(false); Thread.Sleep(300);
            Ok(r.Asked == 1 && r.Offers == 0 && r.H.Files.BytesSent == 0 && !r.H.PullPending && r.Closed == 1, "file ask: Deny -> nothing is offered or sent");
            r.Answer(true); Thread.Sleep(100);
            Ok(r.Offers == 0, "file ask: an Allow after Deny doesn't count");

            // timeout = Deny
            FilePull.TimeoutOverrideMs = 400;
            r = NewHost(Path.Combine(tmp, "timeout"), copied);
            r.Json("t", "clip-files-ask");
            Ok(r.H.PullPending && Wait(() => !r.H.PullPending, 4000) && r.Closed == 1 && r.Offers == 0, "file ask: no answer in time counts as Deny - nothing offered, the question goes away");
            r.Answer(true); Thread.Sleep(200);
            Ok(r.Offers == 0 && r.H.Files.BytesSent == 0, "file ask: an Allow after the timeout doesn't count");
            FilePull.TimeoutOverrideMs = 60000;

            // a 1.5 helper's request (clip-files-get) is asked about too
            r = NewHost(Path.Combine(tmp, "old"), copied);
            r.Json("t", "clip-files-get");
            Ok(r.Asked == 1 && r.Offers == 0, "file ask: a 1.5 helper's request is asked about too (the PC always asks)");
            r.Answer(true);
            Ok(r.Offers == 1, "file ask: ...and Allow sends it");

            // one question at a time
            r = NewHost(Path.Combine(tmp, "twice"), copied);
            r.Json("t", "clip-files-ask"); r.Json("t", "clip-files-ask");
            Ok(r.Asked == 1, "file ask: a second request while asking doesn't stack a second question");

            // clipboard sharing off while asking, or the session ends: the question goes, nothing is sent
            r.H.SetClipboard(false);
            Ok(!r.H.PullPending && r.Closed == 1, "file ask: turning off clipboard sharing closes the question");
            r.Answer(true); Thread.Sleep(100);
            Ok(r.Offers == 0, "file ask: ...and a late Allow sends nothing");
            r = NewHost(Path.Combine(tmp, "end"), copied);
            r.Json("t", "clip-files-ask"); r.H.End("You pressed Ctrl+Alt+End.", false);
            Ok(!r.H.PullPending && r.Closed == 1 && r.H.State == HostState.Ended, "file ask: ending the session (Ctrl+Alt+End) closes the question");
            r.Answer(true); Thread.Sleep(100);
            Ok(r.Offers == 0, "file ask: ...and nothing is sent afterwards");

            // nothing to ask about / not allowed: no question at all
            r = NewHost(Path.Combine(tmp, "none"), new string[0]);
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 0 && r.Offers == 0, "file ask: nothing copied -> no question, nothing sent");
            r = NewHost(Path.Combine(tmp, "noclip"), copied); r.H.Clipboard = false;
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 0 && r.Offers == 0, "file ask: without clipboard sharing -> no question, nothing sent");
            Directory.CreateDirectory(Path.Combine(tmp, "noui")); File.WriteAllLines(Path.Combine(tmp, "noui", "clipboard-files.txt"), copied);
            var bare = new HostSession(new HostOptions { Screen = new SyntheticSource(), FileClip = new FileClip(Path.Combine(tmp, "noui", "clipboard.txt")) }) { State = HostState.Connected };
            bare.Clipboard = true; int bareOffers = 0;
            bare.Files = new Transfers(m => { if (m[0] == Msg.Json && Msg.Str(Msg.ParseJson(m), "t") == "file-batch") bareOffers++; }, () => 0, () => true, Path.Combine(tmp, "noui", "dl")) { PeerBatches = true };
            typeof(HostSession).GetMethod("OnJson", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(bare, new object[] { new Dictionary<string, object> { { "t", "clip-files-ask" } } });
            Ok(bareOffers == 0 && !bare.PullPending, "file ask: with nobody to ask, it's a Deny");
            // the red bar's own "Send copied files" is the person's own action: no extra question
            r = NewHost(Path.Combine(tmp, "own"), copied);
            Ok(r.H.SendCopiedFiles() == null && r.Asked == 0 && r.Offers == 1, "file ask: Send copied files on the red bar (their own click) offers without an extra question");
        }

        static void V16Viewer()
        {
            var v = new ViewerSession(new Uri("wss://assist.example.com/ws"), "481207365", "K7MX2P", "Dom") { State = ViewerState.Connected, ClipboardAllowed = true };
            v.Files.PeerBatches = true; v.HostAsksFiles = false;
            Ok(v.GetCopiedFiles() == "They need Hallow Assist 1.6 to share files this way." && !v.PullWaiting, "older PC: a 1.6 helper won't ask a 1.5 PC for its files");
            v.HostAsksFiles = true;
            Ok(v.GetCopiedFiles() == FilePull.WaitingText && v.PullWaiting, "file ask: the helper sees “Waiting for them to allow…”");
            Ok(v.GetCopiedFiles() == FilePull.WaitingText, "file ask: clicking again while waiting doesn't ask twice");
            v.ClipboardAllowed = false;
            Ok(v.GetCopiedFiles().Contains("clipboard sharing"), "file ask: without clipboard sharing the helper can't ask");
        }
    }
}

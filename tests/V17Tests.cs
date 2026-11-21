// Hallow Assist 1.7 unit tests: in an unattended session (your own PC) "Copy their files here" is allowed without a question
// and reported for the connection history; attended sessions still ask exactly as in 1.6; the setting "Ask before my own
// PCs share files in unattended sessions" brings the question back. Same rig as V16Tests (a real HostSession whose
// file-transfer end records what would leave the PC). Everything goes into a temporary folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static void V17()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "HallowAssistTests17-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try { V17Text(); V17Host(tmp); V17History(tmp); }
            finally { FilePull.TimeoutOverrideMs = 0; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void V17Text()
        {
            Ok(OwnFiles.BarText("Dom") == "Files copied by Dom" && OwnFiles.BarText(null) == "Files copied by your helper", "own PCs: the red bar says “Files copied by <helper>”");
            Ok(OwnFiles.Line(new[] { "a.pdf", "b.zip", "c.txt" }) == "copied 3 files from this PC: a.pdf, b.zip, c.txt" && OwnFiles.Line(new[] { "x.docx" }) == "copied 1 file from this PC: x.docx", "own PCs: the history line says how many files and their names");
            Ok(OwnFiles.Line(Enumerable.Range(1, 9).Select(i => "f" + i).ToArray()).EndsWith("f6 and 3 more"), "own PCs: long lists say how many more");
            var at = new DateTime(2026, 11, 20, 21, 14, 0, DateTimeKind.Local);
            Ok(OwnFiles.Describe(OwnFiles.Note(new[] { "a.pdf" }, at)) == "21:14 copied 1 file from this PC: a.pdf", "own PCs: the history line has the time");
            Ok(FilePull.AnswerText(true, "auto", null).StartsWith("Your own PC sent them by itself") && FilePull.AnswerText(true, "allow", null).StartsWith("They allowed it"), "own PCs: the helper is told their own PC allowed it by itself");
            Ok(OwnFiles.SettingText.StartsWith("Ask before my own PCs share files in unattended sessions") && !new AssistSettings().AskOwnFiles, "own PCs: the setting is off by default");
        }

        static void V17Host(string tmp)
        {
            FilePull.TimeoutOverrideMs = 60000;
            var src = Path.Combine(tmp, "src"); Directory.CreateDirectory(src);
            var f1 = Path.Combine(src, "budget.xlsx"); File.WriteAllText(f1, "own PC file");
            var f2 = Path.Combine(src, "photo.bin"); File.WriteAllBytes(f2, Enumerable.Range(0, 50000).Select(i => (byte)(i % 251)).ToArray());
            var copied = new[] { f1, f2 };

            // unattended: no question, exactly the copied files are offered, the copy is reported for the history
            var r = NewHost(Path.Combine(tmp, "unattended"), copied, new HostOptions { Unattended = true });
            var reported = new List<string[]>(); r.H.FilesCopied += n => reported.Add(n);
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 0 && r.Closed == 0 && !r.H.PullPending, "unattended: no question on your own PC");
            Ok(r.Offers == 1 && r.Batches.Count == 1 && r.Batches[0].SequenceEqual(new[] { "budget.xlsx", "photo.bin" }) && r.H.PullsAuto == 1 && r.H.PullsAllowed == 1, "unattended: exactly the copied files are offered right away");
            Ok(reported.Count == 1 && reported[0].SequenceEqual(new[] { "budget.xlsx", "photo.bin" }), "unattended: the copy is reported for the connection history (names)");
            Ok(r.H.Files.BytesSent == 0, "unattended: ...and the helper's side still asks before anything is sent");
            foreach (var id in new[] { 1, 2 }) r.H.Files.Handle(new Dictionary<string, object> { { "t", "file-accept" }, { "id", id } });
            Ok(Wait(() => r.H.Files.BytesSent == 50000 + "own PC file".Length), "unattended: once the helper accepts, the files go out");
            // a 1.5 helper's request too
            r = NewHost(Path.Combine(tmp, "unattended-old"), copied, new HostOptions { Unattended = true });
            r.Json("t", "clip-files-get");
            Ok(r.Asked == 0 && r.Offers == 1, "unattended: a 1.5 helper's request is allowed by itself too");
            // still only while clipboard sharing is on, and nothing copied -> nothing
            r = NewHost(Path.Combine(tmp, "unattended-noclip"), copied, new HostOptions { Unattended = true }); r.H.Clipboard = false;
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 0 && r.Offers == 0 && r.H.PullsAuto == 0, "unattended: without clipboard sharing nothing is sent");
            r = NewHost(Path.Combine(tmp, "unattended-none"), new string[0], new HostOptions { Unattended = true });
            r.Json("t", "clip-files-ask");
            Ok(r.Offers == 0 && r.H.PullsAuto == 0, "unattended: nothing copied -> nothing sent");

            // attended: asks exactly as in 1.6, nothing reported for the history
            r = NewHost(Path.Combine(tmp, "attended"), copied);
            var rep2 = 0; r.H.FilesCopied += n => rep2++;
            r.Json("t", "clip-files-ask"); Thread.Sleep(200);
            Ok(r.Asked == 1 && r.Offers == 0 && r.H.PullPending && r.H.PullsAuto == 0, "attended: the PC still asks first, nothing offered before Allow");
            r.Answer(true);
            Ok(r.Offers == 1 && rep2 == 0, "attended: Allow sends it (as in 1.6) and nothing goes into the unattended copy notes");
            r = NewHost(Path.Combine(tmp, "attended-deny"), copied);
            r.Json("t", "clip-files-ask"); r.Answer(false); Thread.Sleep(200);
            Ok(r.Asked == 1 && r.Offers == 0, "attended: Deny still sends nothing");

            // the setting on: unattended asks like 1.6
            r = NewHost(Path.Combine(tmp, "setting"), copied, new HostOptions { Unattended = true, AskOwnFiles = true });
            var rep3 = 0; r.H.FilesCopied += n => rep3++;
            r.Json("t", "clip-files-ask"); Thread.Sleep(200);
            Ok(r.Asked == 1 && r.Offers == 0 && r.H.PullPending && r.H.PullsAuto == 0, "setting on: an unattended session asks first again");
            r.Answer(false); Thread.Sleep(100);
            Ok(r.Offers == 0 && rep3 == 0, "setting on: Deny sends nothing");
            r.Json("t", "clip-files-ask"); r.Answer(true);
            Ok(r.Offers == 1 && rep3 == 1, "setting on: Allow sends it and it's still noted in the history (unattended session)");
            // switched on mid-session (Settings while connected)
            r = NewHost(Path.Combine(tmp, "midsession"), copied, new HostOptions { Unattended = true });
            r.H.AskOwnFiles = true; r.Json("t", "clip-files-ask");
            Ok(r.Asked == 1 && r.Offers == 0, "setting on: also for a session that's already connected");
            r.Answer(false); r.H.AskOwnFiles = false; r.Json("t", "clip-files-ask");
            Ok(r.Asked == 1 && r.Offers == 1, "setting off again: allowed by itself");
        }

        static void V17History(string tmp)
        {
            var path = Path.Combine(tmp, "history.json");
            var e = new HistoryEntry { Start = DateTime.Now.AddMinutes(-5).ToString("o"), End = DateTime.Now.ToString("o"), Helper = "Dom", Client = "app", Unattended = true, ControlAllowed = true,
                Copies = new List<CopyNote> { OwnFiles.Note(new[] { "budget.xlsx", "photo.bin" }, DateTime.Now.AddMinutes(-3)) } };
            History.Add(e, path);
            var back = History.Load(path);
            Ok(back.Count == 1 && back[0].Copies != null && back[0].Copies.Count == 1 && back[0].Copies[0].Names.SequenceEqual(new[] { "budget.xlsx", "photo.bin" }), "history: the copy notes are saved and read back");
            var d = History.Describe(back[0], DateTime.Now);
            Ok(d.Contains("unattended") && d.Contains(" copied 2 files from this PC: budget.xlsx, photo.bin") && d.Contains(DateTime.Now.AddMinutes(-3).ToString("HH:mm") + " copied"), "history: the line says what was copied and when (" + d + ")");
            File.WriteAllText(path, "[{\"Start\":\"2026-11-01T10:00:00\",\"End\":\"2026-11-01T10:05:00\",\"Helper\":\"Sam\"}]");
            var old = History.Load(path);
            Ok(old.Count == 1 && old[0].Copies == null && !History.Describe(old[0], DateTime.Now).Contains("copied"), "history: entries from 1.6 still load (no copy notes)");
        }
    }
}

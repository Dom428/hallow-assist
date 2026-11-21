// Hallow Assist 1.9 unit tests: in an unattended session (your own PC) the helper can turn clipboard sharing on and off;
// copied text and files then flow and the red bar says who turned it on. Attended sessions refuse it (only the person at
// the PC turns it on), the setting "Let my own PCs' clipboard be turned on remotely" off refuses it, and a helper facing a
// 1.8 PC says it needs 1.9. Same rig as V16/V17Tests (a real HostSession). Everything goes into a temporary folder.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static void V19()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "HallowAssistTests19-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try { V19Text(); V19Host(tmp); V19Viewer(); }
            finally { FilePull.TimeoutOverrideMs = 0; try { Directory.Delete(tmp, true); } catch { } }
        }

        static void V19Text()
        {
            Ok(RemoteClip.BarText("Dom") == "Clipboard shared (turned on by Dom)" && RemoteClip.BarText(null) == "Clipboard shared (turned on by your helper)", "remote clipboard: the red bar says “Clipboard shared (turned on by <helper>)”");
            var l = HelpBar.Lines("Dom", true, true, true, "4827", true, false, false, "Dom");
            Ok(l[1].EndsWith("Clipboard shared (turned on by Dom)") && !l[1].Contains("clipboard shared"), "remote clipboard: the bar's line names the helper (" + l[1] + ")");
            Ok(HelpBar.Lines("Dom", true, true, true, "4827", true, false)[1].EndsWith("clipboard shared"), "remote clipboard: turned on at the PC -> the bar says clipboard shared, as before");
            Ok(RemoteClip.SettingText.StartsWith("Let my own PCs' clipboard be turned on remotely") && !new AssistSettings().ClipRemoteOff && new HostOptions().ClipRemote, "remote clipboard: the setting is on by default");
            Ok(RemoteClip.OldHostText == "That PC needs Hallow Assist 1.9" && RemoteClip.Refusal("old") == RemoteClip.OldHostText, "remote clipboard: an older PC -> “That PC needs Hallow Assist 1.9”");
            Ok(RemoteClip.Refusal("attended").StartsWith("Only the person at that PC") && RemoteClip.Refusal("setting").Contains("Let my own PCs' clipboard be turned on remotely"), "remote clipboard: the helper is told why it was refused");
        }

        static HostRig ClipHost(string dir, string[] copied, HostOptions o)
        {
            Directory.CreateDirectory(dir);
            o.Clip = new FileClip(Path.Combine(dir, "clipboard.txt"));
            var r = NewHost(dir, copied, o);
            r.H.Clipboard = false;   // a fresh unattended session: clipboard sharing starts off
            return r;
        }
        static string ReadClip(string dir) { var f = Path.Combine(dir, "clipboard.txt"); return File.Exists(f) ? File.ReadAllText(f) : ""; }

        static void V19Host(string tmp)
        {
            FilePull.TimeoutOverrideMs = 60000;
            var src = Path.Combine(tmp, "src"); Directory.CreateDirectory(src);
            var f1 = Path.Combine(src, "notes.txt"); File.WriteAllText(f1, "copied on my own PC");
            var copied = new[] { f1 };

            // unattended: the helper turns it on -> text and files flow, the bar names the helper
            var d = Path.Combine(tmp, "unattended");
            var r = ClipHost(d, copied, new HostOptions { Unattended = true });
            r.Json("t", "clip", "text", "before");
            Ok(ReadClip(d) == "", "unattended: before it's on, the helper's copied text doesn't arrive");
            r.Json("t", "clip-remote", "on", true);
            Ok(r.H.Clipboard && r.H.ClipboardBy == "Dom" && r.H.ClipRemoteAccepted == 1 && r.H.ClipRemoteRefused == 0, "unattended: the helper's request turns clipboard sharing on");
            var bl = HelpBar.Lines(r.H.HelperName, r.H.Control, r.H.Clipboard, r.H.Unattended, "4827", true, false, false, r.H.ClipboardBy);
            Ok(bl[1].Contains("Clipboard shared (turned on by Dom)"), "unattended: the red bar says Clipboard shared (turned on by Dom)");
            r.Json("t", "clip", "text", "hello from the helper");
            Ok(ReadClip(d) == "hello from the helper", "unattended: copied text from the helper arrives on the PC's clipboard");
            r.Json("t", "clip-files-ask");
            Ok(r.Asked == 0 && r.Offers == 1 && r.Batches.Count == 1 && r.Batches[0].SequenceEqual(new[] { "notes.txt" }), "unattended: “Copy their files here” works with nobody there (offered by itself)");
            r.Json("t", "clip-remote", "on", false);
            Ok(!r.H.Clipboard && r.H.ClipboardBy == null && r.H.ClipRemoteAccepted == 2, "unattended: the helper turns it off again");
            r.Json("t", "clip", "text", "after");
            Ok(ReadClip(d) == "hello from the helper", "unattended: once off, copied text no longer arrives");
            // the person at the PC (if there is one) still decides: their own toggle clears "turned on by"
            r.H.RemoteClipboard(true); r.H.SetClipboard(false);
            Ok(!r.H.Clipboard && r.H.ClipboardBy == null, "unattended: Stop clipboard on the red bar still turns it off");
            r.H.SetClipboard(true); r.H.RemoteClipboard(true);
            Ok(r.H.Clipboard && r.H.ClipboardBy == null, "unattended: turned on at the PC stays “clipboard shared” (not credited to the helper)");

            // attended: refused - only the person at the PC can turn it on
            d = Path.Combine(tmp, "attended");
            r = ClipHost(d, copied, new HostOptions());
            var notes = new List<string>(); r.H.Note += n => notes.Add(n);
            var why = r.H.RemoteClipboard(true);
            Ok(why == "attended" && !r.H.Clipboard && r.H.ClipboardBy == null && r.H.ClipRemoteRefused == 1 && r.H.ClipRemoteAccepted == 0, "attended: the helper's request is refused");
            r.Json("t", "clip-remote", "on", true); r.Json("t", "clip", "text", "sneaky");
            Ok(!r.H.Clipboard && ReadClip(d) == "" && r.H.ClipRemoteRefused == 2, "attended: still off, nothing reaches the clipboard");
            Ok(notes.Any(n => n.Contains("would like you to share your clipboard")), "attended: the person is told the helper asked (they click Share clipboard themselves)");
            r.H.SetClipboard(true); r.H.RemoteClipboard(false);
            Ok(r.H.Clipboard && r.H.ClipRemoteRefused == 3, "attended: the helper can't turn it off either - the person's choice stays");

            // the setting off: refused (also when switched off mid-session)
            d = Path.Combine(tmp, "setting");
            r = ClipHost(d, copied, new HostOptions { Unattended = true, ClipRemote = false });
            Ok(r.H.RemoteClipboard(true) == "setting" && !r.H.Clipboard && r.H.ClipRemoteRefused == 1, "setting off: refused");
            r.H.ClipRemote = true;
            Ok(r.H.RemoteClipboard(true) == null && r.H.Clipboard, "setting on again (mid-session): accepted");
            r.H.ClipRemote = false;
            Ok(r.H.RemoteClipboard(false) == "setting" && r.H.Clipboard, "setting off mid-session: further requests are refused");

            // a new session starts with clipboard sharing off and nobody credited
            r = ClipHost(Path.Combine(tmp, "notconnected"), copied, new HostOptions { Unattended = true });
            r.H.State = HostState.Waiting;
            Ok(r.H.RemoteClipboard(true) == "not-connected" && !r.H.Clipboard, "not connected: nothing happens");
        }

        static void V19Viewer()
        {
            // a 1.8 PC doesn't say "clipRemote": the request isn't sent and the helper sees the message
            var v = new ViewerSession(new Uri("wss://assist.example.com/ws"), "715390246", "K7MX2P", "Dom") { State = ViewerState.Connected, HostUnattended = true, HostClipRemote = false };
            Ok(v.SetRemoteClipboard(true) == "That PC needs Hallow Assist 1.9" && v.ClipRemoteLast == "old", "older PC: the helper is told “That PC needs Hallow Assist 1.9”");
            var v2 = new ViewerSession(new Uri("wss://assist.example.com/ws"), "715390246", "K7MX2P", "Dom");
            Ok(v2.SetRemoteClipboard(true) == "Not connected.", "viewer: nothing is sent before the session is connected");
        }
    }
}

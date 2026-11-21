// Hallow Assist 1.6: the helper's "Copy their files here" asks the person being helped first. Before any file leaves their
// PC they see "<helper> wants to copy these N files from your PC: <names, total size>" with Allow / Deny - Deny is the
// default, no answer in 60 s counts as Deny, and Ctrl+Alt+End still ends the session. Only on Allow are exactly the listed
// files offered (the helper's side then still asks before saving anything). A 1.6 helper won't ask a 1.5 PC (which would
// send without asking); a 1.6 PC asks for every request, also from a 1.5 helper.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace HallowAssist
{
    public enum PullResult { Allow, Deny, Timeout, Cancelled }

    public class PullAsk { public string[] Paths, Names; public long Total; public string Skipped = ""; }

    public static class FilePull
    {
        public const string OldPeerText = "They need Hallow Assist 1.6 to share files this way.";
        public const string WaitingText = "Waiting for them to allow…";
        public static int TimeoutOverrideMs;   // unit tests
        public static int TimeoutMs { get { return TimeoutOverrideMs > 0 ? TimeoutOverrideMs : AssistSettings.TestDir != null ? 6000 : 60000; } }

        // the files among the copied paths (folders skipped, the usual limits); an error to tell the helper, or null
        public static string Prepare(string[] paths, out PullAsk ask)
        {
            ask = null; List<FileInfo> files; string skipped;
            var err = Transfers.Pick(paths, out files, out skipped);
            if (err != null) return err;
            ask = new PullAsk { Paths = files.Select(f => f.FullName).ToArray(), Names = files.Select(f => f.Name).ToArray(), Total = files.Sum(f => f.Length), Skipped = skipped.Trim() };
            return null;
        }
        public static string Title(string helper, int n) { return (string.IsNullOrEmpty(helper) ? "Your helper" : helper) + " wants to copy " + (n == 1 ? "this file" : "these " + n + " files") + " from your PC"; }
        public static string List(PullAsk a)
        {
            var shown = a.Names.Take(6).ToList();
            return string.Join(", ", shown) + (a.Names.Length > 6 ? " and " + (a.Names.Length - 6) + " more" : "") + " (" + Transfers.Size(a.Total) + (a.Names.Length > 1 ? " in all" : "") + ")";
        }
        public static string Text(PullAsk a)
        {
            return List(a) + ". Nothing leaves your PC unless you click Allow. Only allow it if you know why they need " + (a.Names.Length == 1 ? "it" : "them") + "." +
                (a.Skipped.Length > 0 ? " " + a.Skipped : "");
        }
        // one answer only: the first of Allow / Deny / the timeout / a cancel counts
        public static Action<PullResult> Once(Action<PullResult> done, int timeoutMs)
        {
            int answered = 0;
            Action<PullResult> a = r => { if (Interlocked.Exchange(ref answered, 1) == 0) done(r); };
            new Thread(() => { Thread.Sleep(timeoutMs); a(PullResult.Timeout); }) { IsBackground = true, Name = "file ask timeout" }.Start();
            return a;
        }
        // the helper's line for an answer from the PC
        public static string AnswerText(bool allowed, string reason, string text)
        {
            if (allowed && reason == "auto") return "Your own PC sent them by itself (unattended session) - you're asked before anything is saved.";   // v1.7
            if (allowed) return "They allowed it - their files are on the way (you're asked before anything is saved).";
            if (!string.IsNullOrEmpty(text)) return text;
            return reason == "timeout" ? "They said no - they didn't answer within a minute, so nothing was sent." : "They said no.";
        }
    }

    // "Dom wants to copy these 3 files from your PC" - Allow / Deny. Deny is the default; no answer in 60 s = Deny.
    public class FileAskWindow : Window
    {
        readonly TextBlock countdown; int left; DispatcherTimer timer; bool done;
        readonly Action<bool> answer;
        public FileAskWindow(string title, string text, Action<bool> answer)
        {
            this.answer = answer; left = Math.Max(1, FilePull.TimeoutMs / 1000);
            Ui.Dress(this, "Hallow Assist - your helper wants your files", 470);
            ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true; ShowInTaskbar = true;
            var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            sp.Children.Add(Ui.Header("ASSIST", 18)); sp.Children[0].SetValue(MarginProperty, new Thickness(0, 0, 0, 14));
            sp.Children.Add(Ui.T(title, 17, true, null, 8));
            var lb = new Border { Background = Ui.R("PanelHi"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10) };
            lb.Child = Ui.T(text, 12.5); ((TextBlock)lb.Child).TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(lb);
            sp.Children.Add(Ui.Muted("Ctrl+Alt+End still ends the whole session.", 11.5, 4));
            var row = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            countdown = Ui.Muted("", 11.5); countdown.VerticalAlignment = VerticalAlignment.Center;
            var deny = Ui.Btn("Deny", () => Finish(false)); deny.IsCancel = true; deny.IsDefault = true; deny.MinWidth = 90;
            var allow = Ui.Btn("Allow", () => Finish(true), true); allow.MinWidth = 90; allow.Margin = new Thickness(0);
            var btns = Ui.Row(deny, allow); DockPanel.SetDock(btns, Dock.Right);
            row.Children.Add(btns); row.Children.Add(countdown);
            sp.Children.Add(row);
            Content = sp;
            Tick();
            if (!Snap.On) { timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (s, e) => { left--; Tick(); if (left <= 0) Finish(false); }; timer.Start(); }
            Closing += (s, e) => { if (!done) { done = true; if (timer != null) timer.Stop(); answer(false); } };
            Loaded += (s, e) => deny.Focus();
        }
        void Tick() { countdown.Text = "Closes as Deny in " + left + " s"; }
        void Finish(bool allow) { if (done) return; done = true; if (timer != null) timer.Stop(); answer(allow); Close(); }
        public void Cancel() { if (done) return; done = true; if (timer != null) timer.Stop(); answer(false); Close(); }
    }
}

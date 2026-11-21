// Hallow Assist 1.9: in an UNATTENDED session (one of your own PCs, connected with its unattended password) clipboard
// sharing starts off and nobody is there to click Share clipboard - so the HELPER can turn it on and off from the viewer.
// The viewer sends a sealed "clip-remote" request; the PC accepts it only when the session is unattended and its setting
// "Let my own PCs' clipboard be turned on remotely" (on by default) is on. The red bar then says "Clipboard shared (turned
// on by <helper>)". Attended sessions are unchanged: only the person at the PC can turn it on. A PC with 1.8 or earlier
// doesn't say it understands the request ("clipRemote" in the consent message), so the helper's app says it needs 1.9.
namespace HallowAssist
{
    public static class RemoteClip
    {
        public const string SettingText = "Let my own PCs' clipboard be turned on remotely (your helper app can turn clipboard sharing on and off in unattended sessions) - on by default";
        public const string OldHostText = "That PC needs Hallow Assist 1.9";
        public const string AttendedText = "Only the person at that PC can turn on clipboard sharing - this isn't an unattended session on one of your own PCs.";
        public const string SettingOffText = "That PC doesn't let its clipboard be turned on remotely (its Settings: \"Let my own PCs' clipboard be turned on remotely\").";
        public const string ToggleTip = "Your own PCs only (unattended sessions): turn clipboard sharing on that PC on or off from here, so copied text and \"Copy their files here\" work with nobody there. Their red bar says who turned it on.";
        public static string BarText(string helper) { return "Clipboard shared (turned on by " + (string.IsNullOrEmpty(helper) ? "your helper" : helper) + ")"; }
        public static string ToggleLabel(bool on) { return on ? "Their clipboard: on" : "Their clipboard: off"; }
        public static string AnswerText(bool on) { return on ? "Their clipboard is shared now - copied text and files can go both ways." : "Their clipboard isn't shared any more."; }
        public static string Refusal(string reason)
        {
            switch (reason)
            {
                case "attended": return AttendedText;
                case "setting": return SettingOffText;
                case "old": return OldHostText;
                default: return "That PC didn't turn on clipboard sharing.";
            }
        }
        // the PC's own line (the main window and the log)
        public static string HostNote(string helper, bool on) { return (string.IsNullOrEmpty(helper) ? "Your helper" : helper) + (on ? " turned on clipboard sharing (unattended session) - you can stop it on the red bar." : " turned off clipboard sharing."); }
        public static string AskedNote(string helper) { return (string.IsNullOrEmpty(helper) ? "Your helper" : helper) + " would like you to share your clipboard - click Share clipboard on the red bar if that's OK."; }
    }
}

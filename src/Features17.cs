// Hallow Assist 1.7: in an UNATTENDED session (one of your own PCs, connected with its unattended password) the helper's
// "Copy their files here" doesn't ask on that PC - it allows it by itself, writes "copied N files from this PC: <names>"
// with the time into that PC's connection history, and the red bar (if it's on screen) briefly says "Files copied by
// <helper>". Attended sessions ask exactly as in 1.6. Settings > "Ask before my own PCs share files in unattended
// sessions" (off by default) brings the 1.6 question back for unattended sessions too.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace HallowAssist
{
    // one copy out of this PC, kept in the session's history entry
    [DataContract]
    public class CopyNote
    {
        [DataMember] public string At;          // ISO time
        [DataMember] public string[] Names;
    }

    public static class OwnFiles
    {
        public const string SettingText = "Ask before my own PCs share files in unattended sessions - off by default";
        public const int FlashSeconds = 8;
        public static string BarText(string helper) { return "Files copied by " + (string.IsNullOrEmpty(helper) ? "your helper" : helper); }
        // "copied 3 files from this PC: a.pdf, b.zip, c.txt" (the first 6 names, then "and N more")
        public static string Line(string[] names)
        {
            names = names ?? new string[0];
            var shown = names.Take(6).ToList();
            return "copied " + (names.Length == 1 ? "1 file" : names.Length + " files") + " from this PC: " + string.Join(", ", shown) + (names.Length > 6 ? " and " + (names.Length - 6) + " more" : "");
        }
        // "21:14 copied 3 files from this PC: ..." for the history window
        public static string Describe(CopyNote c)
        {
            DateTime d; var t = DateTime.TryParse(c.At, null, System.Globalization.DateTimeStyles.RoundtripKind, out d) ? d.ToString("HH:mm") + " " : "";
            return t + Line(c.Names);
        }
        public static CopyNote Note(string[] names, DateTime at) { return new CopyNote { At = at.ToString("o"), Names = (names ?? new string[0]).ToArray() }; }
    }
}

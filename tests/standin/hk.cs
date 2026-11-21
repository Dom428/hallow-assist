// A stand-in for Hallow Keys' hk.exe, for Hallow Assist's tests only. It never talks to Hallow Keys or reads a vault: it takes
// the same arguments as the real one (hk --get "<item>" --field password) and answers with the same exit codes.
//   item "code-N"  -> exits with code N (1 usage .. 7 other) and hk's kind of message on stderr
//   item "hang"    -> waits 10 minutes (the caller's timeout must stop it)
//   item "empty"   -> exit 0 with nothing on stdout
//   otherwise      -> looks the item up in HK_STANDIN_ITEMS (lines "name<TAB>made-up password") and prints the password
//                     (no newline when the output is redirected, like the real one), or exit 4
// Every call appends its arguments (never a password) to HK_STANDIN_LOG when that's set.
using System;
using System.IO;
using System.Text;

public static class HkStandIn
{
    public static int Main(string[] args)
    {
        var log = Environment.GetEnvironmentVariable("HK_STANDIN_LOG");
        if (!string.IsNullOrEmpty(log)) try { File.AppendAllText(log, string.Join(" | ", args) + Environment.NewLine); } catch { }
        string item = null, field = null;
        if (args.Length == 0 || args[0] != "--get") return Fail(1, "usage: hk --get \"<item>\" [--field <field>]");
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--field" && i + 1 < args.Length) field = args[++i];
            else if (item == null && !args[i].StartsWith("--")) item = args[i];
            else return Fail(1, "unknown argument " + args[i]);
        }
        if (item == null) return Fail(1, "usage: hk --get \"<item>\" [--field <field>]");
        if (field != null && field != "password") return Fail(4, "no field " + field);
        if (item.StartsWith("code-"))
        {
            int c; int.TryParse(item.Substring(5), out c);
            var msgs = new[] { "", "usage", "Hallow Keys isn't running (or is older than 2.6)", "Hallow Keys is locked", "no item called " + item, "not allowed", "the command line is turned off in Hallow Keys", "something went wrong" };
            return Fail(c, c >= 0 && c < msgs.Length ? msgs[c] : "error");
        }
        if (item == "hang") { System.Threading.Thread.Sleep(600000); return 5; }
        if (item == "empty") return 0;
        var items = Environment.GetEnvironmentVariable("HK_STANDIN_ITEMS");
        if (!string.IsNullOrEmpty(items) && File.Exists(items))
            foreach (var line in File.ReadAllLines(items, Encoding.UTF8))
            {
                var tab = line.IndexOf('\t'); if (tab <= 0) continue;
                if (line.Substring(0, tab) != item) continue;
                var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
                o.Write(line.Substring(tab + 1)); if (!Console.IsOutputRedirected) o.WriteLine();
                return 0;
            }
        return Fail(4, "no item called " + item);
    }
    static int Fail(int code, string msg) { Console.Error.WriteLine("hk: " + msg); return code; }
}

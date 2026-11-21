// Hallow Assist 1.10: Your PCs' online state for the other Hallow apps (Hallow Remote and Hallow Hub on your phone).
//  - status.json (and the "status" pipe reply) carry "pcs": [{ "name", "online": waiting|busy|offline|unknown, "checked" }] -
//    the saved name and the online dot only: never a code, password, key, network adapter (MAC) or address.
//  - The dots come from the relay's "status" hello (1.2). It ran only while the window was open; now it also runs every
//    2 minutes while Assist sits in the tray with a relay set.
//  - Pipe command "connect <name>": opens the window with that PC selected. It never starts a session - you click Connect.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HallowAssist
{
    public static class PcShare
    {
        public const int TraySeconds = 120;   // how often the dots are refreshed while the window is closed

        // the relay's answer (online / busy / offline, or nothing) -> what status.json says
        public static string State(string relay)
        {
            return relay == "online" ? "waiting" : relay == "busy" ? "busy" : relay == "offline" ? "offline" : "unknown";
        }

        // Your PCs in the window's order (last used first, then by name): name, online state and when it was checked (null = never)
        public static object[] List(IEnumerable<SavedPc> pcs, IDictionary<string, string> status, IDictionary<string, DateTime> checkedAt)
        {
            if (pcs == null) return new object[0];
            return Ordered(pcs).Select(p =>
            {
                string st = null; if (status != null && p.Code != null) status.TryGetValue(p.Code, out st);
                DateTime when = DateTime.MinValue; bool has = checkedAt != null && p.Code != null && checkedAt.TryGetValue(p.Code, out when);
                return (object)new Dictionary<string, object> { { "name", p.Name ?? "" }, { "online", has ? State(st) : "unknown" }, { "checked", has ? Family.Time(when) : null } };
            }).ToArray();
        }
        public static IEnumerable<SavedPc> Ordered(IEnumerable<SavedPc> pcs) { return pcs.OrderByDescending(p => p.LastUsed ?? "").ThenBy(p => p.Name); }

        // time for another look? While the window is open: on every tick (as in 1.2). In the tray: every 2 minutes.
        public static bool Due(bool windowOpen, DateTime lastUtc, DateTime nowUtc)
        {
            return windowOpen || (nowUtc - lastUtc).TotalSeconds >= TraySeconds - 1;
        }

        // "connect <name>": the saved name, ignoring case and spaces around it (the first in the window's order if two share it)
        public static SavedPc Find(IEnumerable<SavedPc> pcs, string name)
        {
            if (pcs == null || string.IsNullOrWhiteSpace(name)) return null;
            var n = name.Trim();
            return Ordered(pcs).FirstOrDefault(p => string.Equals((p.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
        }

        public static string SelectedText(SavedPc p)
        {
            return p.Name + " is selected - click Connect next to it" + (p.Key == null && p.KeysItem == null ? " and type its unattended password" : "") + ". Nothing starts until you do.";
        }
    }
}

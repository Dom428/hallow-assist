// Hallow Assist 1.10 unit tests: Your PCs' online state in status.json ("pcs": name, online, checked - never a code,
// password, key, MAC or address), the 2-minute tray refresh timing, and "connect <name>" finding the PC by its saved name.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static void V110()
        {
            Ok(PcShare.State("online") == "waiting" && PcShare.State("busy") == "busy" && PcShare.State("offline") == "offline" && PcShare.State(null) == "unknown" && PcShare.State("odd") == "unknown",
                "pcs: the relay's online / busy / offline -> waiting / busy / offline, anything else unknown");
            var pcs = new List<SavedPc> {
                new SavedPc { Name = "Living room PC", Code = "715390246", Key = "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA-secret-key", LastUsed = "2026-11-20T10:00:00.0000000+01:00", Mac = "3C7C3F1A2B4D" },
                new SavedPc { Name = "Mum's laptop", Code = "302118457" },
                new SavedPc { Name = "Office PC", Code = "560913284", Mac = "02005E102030", KeysItem = "Office PC (Assist)" },
                new SavedPc { Name = "Garage", Code = "111222333" } };
            var status = new Dictionary<string, string> { { "715390246", "online" }, { "302118457", "busy" }, { "560913284", "offline" } };
            var t = new DateTime(2026, 11, 21, 9, 30, 5);
            var checkedAt = new Dictionary<string, DateTime> { { "715390246", t }, { "302118457", t }, { "560913284", t } };
            var list = PcShare.List(pcs, status, checkedAt).Cast<Dictionary<string, object>>().ToList();
            Ok(list.Count == 4 && (string)list[0]["name"] == "Living room PC", "pcs: every saved PC, in the window's order (last used first)");
            Ok(list.All(d => string.Join(",", d.Keys) == "name,online,checked"), "pcs: exactly name, online, checked");
            var by = list.ToDictionary(d => (string)d["name"]);
            Ok((string)by["Living room PC"]["online"] == "waiting" && (string)by["Mum's laptop"]["online"] == "busy" && (string)by["Office PC"]["online"] == "offline", "pcs: waiting / busy / offline from the dots");
            Ok((string)by["Garage"]["online"] == "unknown" && by["Garage"]["checked"] == null, "pcs: never checked -> unknown, checked null");
            Ok((string)by["Office PC"]["checked"] == "2026-11-21T09:30:05", "pcs: checked is an ISO time (" + by["Office PC"]["checked"] + ")");
            var checkedOnly = new Dictionary<string, DateTime> { { "111222333", t } };
            Ok((string)PcShare.List(pcs, new Dictionary<string, string>(), checkedOnly).Cast<Dictionary<string, object>>().First(d => (string)d["name"] == "Garage")["online"] == "unknown",
                "pcs: checked but the relay couldn't be asked -> unknown");
            var json = Family.Json(new Dictionary<string, object> { { "pcs", PcShare.List(pcs, status, checkedAt) } });
            Ok(new[] { "715390246", "715 390 246", "302118457", "560913284", "111222333", "3C7C3F1A2B4D", "02005E102030", "secret-key", "AQAAANC", "Office PC (Assist)\"" }.All(s => !json.Contains(s)),
                "pcs: no code, key, MAC or Keys item in the JSON");
            Ok(PcShare.List(null, null, null).Length == 0 && PcShare.List(new List<SavedPc>(), status, checkedAt).Length == 0, "pcs: none saved -> []");

            // the tray refresh: every 2 minutes with the window closed, every tick with it open
            var now = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
            Ok(PcShare.Due(false, DateTime.MinValue, now), "refresh: never checked -> due");
            Ok(!PcShare.Due(false, now.AddSeconds(-60), now), "refresh: in the tray, 60 s after the last look -> not yet");
            Ok(PcShare.Due(false, now.AddSeconds(-120), now), "refresh: in the tray, 2 minutes after -> due");
            Ok(PcShare.Due(true, now.AddSeconds(-5), now), "refresh: window open -> every tick, as in 1.2");

            // connect <name>
            Ok(PcShare.Find(pcs, "Office PC").Code == "560913284" && PcShare.Find(pcs, "  mum's LAPTOP ").Code == "302118457", "connect: finds the PC by its saved name (case and spaces ignored)");
            Ok(PcShare.Find(pcs, "Office") == null && PcShare.Find(pcs, "") == null && PcShare.Find(pcs, null) == null && PcShare.Find(null, "x") == null, "connect: no partial names, nothing for empty");
            Ok(PcShare.Find(pcs, "560913284") == null, "connect: a code is not a name");
            var twins = new List<SavedPc> { new SavedPc { Name = "PC", Code = "100000001" }, new SavedPc { Name = "PC", Code = "100000002", LastUsed = "2026-11-21T08:00:00" } };
            Ok(PcShare.Find(twins, "PC").Code == "100000002", "connect: two PCs with one name -> the first in the window (last used)");
            Ok(PcShare.SelectedText(pcs[1]).Contains("click Connect") && PcShare.SelectedText(pcs[1]).Contains("type its unattended password") && !PcShare.SelectedText(pcs[0]).Contains("type"),
                "connect: the window says to click Connect (and to type the password only when it isn't remembered)");
        }
    }
}

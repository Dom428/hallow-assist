// Hallow Assist 1.11 unit tests: light / dark mode - the mode from theme.json (missing = dark), "system" through a fake
// Windows answer, Assist's own Look setting winning, the light theme having none of the dark colours left, live re-theming
// colour swaps, and contrast of every text / background pair in the light palette.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static void V111()
        {
            // mode from theme.json
            Ok(Look.ParseMode(null) == "dark" && Look.ParseMode("") == "dark", "mode: no theme.json -> dark");
            Ok(Look.ParseMode("{\"accent\":\"#8B5CF6\",\"updated\":\"2026-11-21T10:00:00Z\"}") == "dark", "mode: theme.json without mode -> dark");
            Ok(Look.ParseMode("{\"accent\":\"#8B5CF6\",\"mode\":\"light\"}") == "light" && Look.ParseMode("{\"mode\":\"system\",\"extra\":{\"x\":1}}") == "system", "mode: light / system read, unknown fields tolerated");
            Ok(Look.ParseMode("{\"mode\":\"LIGHT\"}") == "light" && Look.ParseMode("{\"mode\":\"sepia\"}") == "dark" && Look.ParseMode("{\"mode\":3}") == "dark", "mode: case-insensitive; unknown values -> dark");
            Ok(Look.ParseMode("{not json") == "dark", "mode: broken file -> dark, no crash");

            // resolution: per-app override wins; system follows the (fake) Windows setting
            Func<bool?> winLight = () => true, winDark = () => false, winNone = () => null, winThrows = () => { throw new InvalidOperationException(); };
            Ok(!Look.Resolve("hallow", "dark", winLight) && Look.Resolve("hallow", "light", winDark), "resolve: follow Hallow uses the family mode");
            Ok(Look.Resolve("hallow", "system", winLight) && !Look.Resolve("hallow", "system", winDark), "resolve: system = Windows' AppsUseLightTheme (fake)");
            Ok(!Look.Resolve("hallow", "system", winNone) && !Look.Resolve("hallow", "system", winThrows), "resolve: system with no answer / an error -> dark");
            Ok(Look.Resolve("light", "dark", winDark) && !Look.Resolve("dark", "light", winLight) && !Look.Resolve("dark", "system", winLight), "resolve: Assist's own Light / Dark wins over Hallow and Windows");
            Ok(Look.NormApp(null) == "hallow" && Look.NormApp("weird") == "hallow" && Look.NormApp("Light") == "light", "setting: missing / unknown -> Follow Hallow");
            Ok(MainWindow.LookValues.SequenceEqual(new[] { "hallow", "light", "dark" }) && MainWindow.LookChoices[0] == "Follow Hallow", "setting: Follow Hallow (default) / Light / Dark");
            Ok(Look.SystemLight() == null || Look.SystemLight() != null, "system: reading the real Windows setting doesn't throw (read only)");

            // the light theme
            string xaml;
            using (var s = typeof(Look).Assembly.GetManifestResourceStream("HallowAssist.Theme.xaml")) using (var r = new StreamReader(s)) xaml = r.ReadToEnd();
            var light = Look.BuildXaml(xaml, Accent.Default, true); var dark = Look.BuildXaml(xaml, Accent.Default, false);
            var left = Look.Palette.Keys.Where(k => light.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0 && !Look.Palette.Values.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            Ok(left.Count == 0, "light theme: no dark colour left (" + string.Join(",", left) + ")");
            Ok(light.Contains("#F6F4FA") && light.Contains("#1E1A26") && light.Contains("#FFFFFF"), "light theme: spec background / text / panel");
            var themeHex = System.Text.RegularExpressions.Regex.Matches(xaml, "#[0-9A-Fa-f]{6}\\b").Cast<System.Text.RegularExpressions.Match>().Select(m => m.Value.ToUpperInvariant()).Distinct();
            var unmapped = themeHex.Where(h => !Look.Palette.ContainsKey(h) && h != "#F07820" && h != "#F07821" && h != "#F59A50" && h != "#1A0E04" && h != "#FFFFFF").ToList();
            Ok(unmapped.Count == 0, "Theme.xaml: every colour has a light value (" + string.Join(",", unmapped) + ")");
            Ok(!dark.Contains("#F07821") && !light.Contains("#F07821") && dark.Replace("#FFFFFE", "#FFFFFF") == xaml.Replace("#F07821", "#F07820").Replace("#FFFFFE", "#FFFFFF"), "dark theme: unchanged from before (only the markers resolved)");
            var lightPurple = Look.BuildXaml(xaml, "#8B5CF6", true);
            Ok(lightPurple.Contains("\"CaretBrush\" Value=\"" + Look.TextOn("#8B5CF6", "#F6F4FA") + "\"") && lightPurple.Contains("Value=\"#8B5CF6\""), "light theme: caret / focus line use the darkened accent, filled buttons the real one");
            Ok(Look.Map2("#2A1A20") == "#2A1A20", "the red bar's dark red isn't in the light palette (it stays red)");

            // accent as text: darkened in HSL until >= 4.5:1 on white (default orange -> about #B85410)
            var ot = Look.TextOn("#F07820");
            Ok(Look.Contrast(ot, "#FFFFFF") >= 4.5 && Look.Contrast(ot, "#FFFFFF") < 4.8, "accent text: default orange -> " + ot + " (" + Look.Contrast(ot, "#FFFFFF").ToString("0.00") + ":1 on white)");
            Ok(Look.TextOn("#1E1A26") == "#1E1A26", "accent text: an already dark colour is kept");

            // contrast: every text / background pair in the light palette
            var bgs = new[] { "#FFFFFF", "#F6F4FA" };
            var texts = new Dictionary<string, string> { { "text", "#1E1A26" }, { "muted", "#6B6478" }, { "lilac", "#7A5CC0" } };
            foreach (var st in new[] { "#6ED28C", "#F0BE5A", "#E05A64" }) texts["status " + Look.Palette[st]] = Ui.CodeColor(st, true, Accent.Default);
            foreach (var a in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FFD400", "#00BCD4", "#EC4899", "#FFFFFF" }) texts["accent " + a] = Ui.CodeColor("ACCENT", true, a);
            var bad = new List<string>();
            foreach (var t in texts) foreach (var b in bgs) if (Look.Contrast(t.Value, b) < 4.5) bad.Add(t.Key + " " + t.Value + " on " + b);
            foreach (var t in new[] { "#1E1A26", "#6B6478" }) if (Look.Contrast(t, "#EEEAF5") < 4.5) bad.Add(t + " on panel 2");
            if (Look.Contrast(Ui.CodeColor("#F0BE5A", true, Accent.Default), "#FBF0D9") < 4.5) bad.Add("amber on the notice");
            foreach (var a in new[] { "#F07820", "#8B5CF6", "#FFD400" }) if (Look.Contrast(Ui.CodeColor("ACCENT", true, a), "#EEEAF5") < 3) bad.Add("large accent text on panel 2 " + a);
            if (Look.Contrast("#1E1A26", "#F6D5D8") < 4.5) bad.Add("text on the pressed toggle");
            foreach (var ic in new[] { "#8F88A0", "#1E8E4F", "#C2333F" }) if (Look.Contrast(ic, "#FFFFFF") < 3) bad.Add("icon / switch " + ic);
            if (Look.Contrast("#1E8E4F", "#FFFFFF") < 3) bad.Add("switch knob on green");
            Ok(bad.Count == 0, "contrast: light text >= 4.5:1, large text / icons >= 3:1 (" + string.Join("; ", bad) + ")");
            bad.Clear();
            foreach (var a in new[] { "#F07820", "#8B5CF6", "#22C55E", "#FFD400", "#00BCD4" })
            {
                var on = Accent.OnAccent(a); var other = on == "#FFFFFF" ? "#1A0E04" : "#FFFFFF";
                if (Look.Contrast(on, a) < Look.Contrast(other, a)) bad.Add("button text on " + a);
            }
            Ok(bad.Count == 0 && Look.Contrast(Accent.OnAccent("#F07820"), "#F07820") >= 4.5, "filled buttons: the real accent with whichever text reads better");

            // live re-theming: the colours the code set swap both ways
            var toLight = Ui.ColorMap(false, Accent.Default, true, Accent.Default);
            Ok(toLight["#16121C"] == "#F6F4FA" && toLight["#ECE8F4"] == "#1E1A26" && toLight["#F07820"] == Look.TextOn("#F07820", "#F6F4FA") && toLight["#6ED28C"] == Ui.CodeColor("#6ED28C", true, Accent.Default), "live: dark -> light colour swaps");
            var toDark = Ui.ColorMap(true, Accent.Default, false, Accent.Default);
            Ok(toDark["#FFFFFF"] == "#211B2A" && toDark["#EEEAF5"] == "#2E2639" && toDark["#1E1A26"] == "#ECE8F4" && toDark[Look.TextOn("#F07820", "#F6F4FA")] == "#F07820", "live: light -> dark colour swaps (white -> panel)");
            Ok(Ui.ColorMap(false, Accent.Default, false, Accent.Default).Count == 0, "live: nothing changes when nothing changed");
            var acc = Ui.ColorMap(false, "#F07820", false, "#8B5CF6");
            Ok(acc.Count == 1 && acc["#F07820"] == "#8B5CF6", "live: an accent change swaps only the accent");

            // settings: the Look survives a save / load and an old file without it = Follow Hallow
            var json = "{\"RelayUrl\":\"\",\"Look\":\"light\"}";
            var ser = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(AssistSettings));
            var st2 = (AssistSettings)ser.ReadObject(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
            Ok(st2.AppLook == "light", "settings: \"Look\" read from settings.json");
            var st3 = (AssistSettings)ser.ReadObject(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"RelayUrl\":\"\"}")));
            typeof(AssistSettings).GetMethod("Fix", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(st3, null);
            Ok(st3.AppLook == "hallow", "settings: an old settings.json -> Follow Hallow");
            Ok(Program.Version == "1.11", "version 1.11");
        }
    }
}

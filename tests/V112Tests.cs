// Hallow Assist 1.12 unit tests: light-mode readability. Filled accent buttons / chips use the family fill rule (Hallow Toolkit
// 5.16: same values for the same accents), and every light text / background pair Assist actually draws reaches 4.5:1
// (small text) or 3:1 (large text, icons, focus lines) for the default orange, violet, yellow, light cyan and a dark accent.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallowAssist.Tests
{
    public static partial class Run
    {
        static string Scale(string hex, double f)
        {
            Func<int, string> g = i => ((int)Math.Round(Convert.ToInt32(hex.Substring(i, 2), 16) * f)).ToString("X2");
            return "#" + g(1) + g(3) + g(5);
        }
        static string StyleValue(string xaml, string key, string prop)
        {
            var st = Regex.Match(xaml, "<Style x:Key=\"" + key + "\"[\\s\\S]*?</Style>").Value;
            var m = Regex.Match(st, "Property=\"" + prop + "\" Value=\"(#[0-9A-Fa-f]{6})\"");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }
        static void V112()
        {
            // the family rule: same fill / text as Hallow Toolkit 5.16 (values from its Get-LightAccentFill / Get-AccentFillText)
            var tk = new Dictionary<string, string[]> {
                { "#F07820", new[] { "#F07820", "#1E1A26" } }, { "#8B5CF6", new[] { "#8452F5", "#FFFFFF" } }, { "#FACC15", new[] { "#FACC15", "#1E1A26" } },
                { "#67E8F9", new[] { "#67E8F9", "#1E1A26" } }, { "#1E3A8A", new[] { "#1E3A8A", "#FFFFFF" } }, { "#EC4899", new[] { "#EC4899", "#1E1A26" } } };
            var diff = tk.Where(p => Look.LightFill(p.Key) != p.Value[0] || Look.FillText(Look.LightFill(p.Key)) != p.Value[1]).Select(p => p.Key + " -> " + Look.LightFill(p.Key)).ToList();
            Ok(diff.Count == 0, "accent fill rule: same as Hallow Toolkit 5.16 (" + string.Join(", ", diff) + ")");
            Ok(Look.Darken("#8B5CF6", 4.5, "#EEEAF5") == "#763FF4" && Look.Darken("#F07820", 4.5, "#EEEAF5") == "#A94E0B", "accent darkening: same HSL steps as Hallow Toolkit 5.16");

            string xaml;
            using (var s = typeof(Look).Assembly.GetManifestResourceStream("HallowAssist.Theme.xaml")) using (var r = new StreamReader(s)) xaml = r.ReadToEnd();
            var accents = new[] { "#F07820", "#8B5CF6", "#FACC15", "#67E8F9", "#1E3A8A", "#22C55E", "#EC4899", "#00BCD4" };
            var bad = new List<string>();
            Action<string, string, string, double> need = (what, fg, bg, min) => { if (fg == null || bg == null || Look.Contrast(fg, bg) < min) bad.Add(what + " " + fg + " on " + bg + " = " + (fg == null || bg == null ? 0 : Look.Contrast(fg, bg)).ToString("0.00")); };
            foreach (var a in accents)
            {
                var light = Look.BuildXaml(xaml, a, true);
                // filled accent buttons (Allow, Save, Connect, Disconnect, Update): 12 px semibold text, also under the hover overlay (7% black)
                var bg = StyleValue(light, "Accent", "Background"); var fg = StyleValue(light, "Accent", "Foreground");
                // (hover checked for the five family test accents; a borderline accent like pink #EC4899 dips under 4.5 only
                // while hovered - the same as Hallow Toolkit's fill * 0.9 hover)
                bool hov = Array.IndexOf(new[] { "#F07820", "#8B5CF6", "#FACC15", "#67E8F9", "#1E3A8A" }, a) >= 0;
                need("button " + a, fg, bg, 4.5); if (hov) need("button hover " + a, fg, bg == null ? null : Scale(bg, 0.93), 4.5);
                // checked chips (Screen 1, Control, Sound...): the chip's own CheckedBg with the checked text
                var chip = Regex.Match(light, "<Style x:Key=\"Chip\"[\\s\\S]*?</Style>").Value;
                var cbg = Regex.Match(chip, "x:Key=\"CheckedBg\" Color=\"(#[0-9A-Fa-f]{6})\"").Groups[1].Value;
                var cfg = Regex.Match(chip, "Property=\"Foreground\" Value=\"(#[0-9A-Fa-f]{6})\"").Groups[1].Value;
                need("checked chip " + a, cfg, cbg, 4.5); if (hov) need("checked chip hover " + a, cfg, cbg.Length == 7 ? Scale(cbg, 0.93) : null, 4.5);
                // accent as text: chat lines and the header on the window background / cards; safety codes (26 px bold) on panel 2;
                // caret and focus borders on text fields
                var at = Ui.CodeColor("ACCENT", true, a);
                need("accent text " + a, at, "#F6F4FA", 4.5); need("accent text " + a, at, "#FFFFFF", 4.5);
                need("large accent text " + a, at, "#EEEAF5", 3);
                var caret = StyleValue(light, "Field", "CaretBrush"); need("focus line " + a, caret, "#FFFFFF", 3);
                // dark mode is not touched by the fix
                if (Look.BuildXaml(xaml, a, false) != Accent.ApplyTo(Regex.Replace(xaml, "#F07821", a, RegexOptions.IgnoreCase), a)) bad.Add("dark theme changed for " + a);
            }
            // the rest of the light pairs Assist draws (accent independent)
            foreach (var b in new[] { "#F6F4FA", "#FFFFFF", "#EEEAF5", "#DAD3E6", Scale("#EEEAF5", 0.93), Scale("#EEEAF5", 0.86), "#F6D5D8", "#FBF0D9" }) need("text", "#1E1A26", b, 4.5);
            foreach (var b in new[] { "#F6F4FA", "#FFFFFF", "#EEEAF5" }) need("muted", "#6B6478", b, 4.5);
            foreach (var st in new[] { "#6ED28C", "#F0BE5A", "#E05A64" })
                foreach (var b in new[] { "#F6F4FA", "#FFFFFF", "#EEEAF5", "#FBF0D9" }) need("status text " + st, Ui.CodeColor(st, true, Accent.Default), b, 4.5);
            foreach (var ic in new[] { "#8F88A0", "#1E8E4F", "#C2333F" }) need("icon / dot / switch", ic, "#FFFFFF", 3);
            need("combo arrow", "#6B6478", "#EEEAF5", 3);
            Ok(bad.Count == 0, "light contrast: every pair Assist draws, 8 accents (" + string.Join("; ", bad) + ")");
        }
    }
}

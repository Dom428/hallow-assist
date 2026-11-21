// Hallow Assist 1.11 - light and dark mode (family spec): "mode" in %APPDATA%\Hallow\theme.json (written by Hallow Toolkit,
// only READ here) = dark / light / system; missing = dark. Assist's own Look setting (Follow Hallow / Light / Dark) wins.
// "system" follows Windows' AppsUseLightTheme. Dark = the look Assist always had; light swaps the theme's colours.
// The red "someone can see your screen" bar stays dark red in both modes.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HallowAssist
{
    public static class Look
    {
        public static bool Light;   // the mode in use right now
        // tests swap this for a fake; null = Windows has no answer (treated as dark)
        public static Func<bool?> SystemLight = ReadSystemLight;

        static bool? ReadSystemLight()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int) return (int)v == 1;
                }
            }
            catch { }
            return null;
        }

        // "dark" / "light" / "system" from theme.json; missing, broken or unknown = "dark"
        public static string ReadFamilyMode() { return ParseMode(ReadText()); }
        public static string ParseMode(string json)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return "dark";
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object m;
                var s = d != null && d.TryGetValue("mode", out m) ? (m as string ?? "").Trim().ToLowerInvariant() : "";
                return s == "light" || s == "system" ? s : "dark";
            }
            catch { return "dark"; }
        }
        static string ReadText()
        {
            try
            {
                if (!File.Exists(Accent.FilePath)) return null;
                for (int i = 0; i < 3; i++) { try { return File.ReadAllText(Accent.FilePath); } catch (IOException) { System.Threading.Thread.Sleep(50); } }
            }
            catch { }
            return null;
        }
        // Assist's own setting: "hallow" (follow Hallow, the default) / "light" / "dark"
        public static string NormApp(string s) { s = (s ?? "").Trim().ToLowerInvariant(); return s == "light" || s == "dark" ? s : "hallow"; }
        public static bool Resolve(string appLook, string familyMode, Func<bool?> system)
        {
            var a = NormApp(appLook);
            if (a != "hallow") return a == "light";
            if (familyMode == "light") return true;
            if (familyMode == "system") { bool? l = null; try { l = system != null ? system() : null; } catch { } return l == true; }
            return false;
        }
        public static bool Current(string appLook) { return Resolve(appLook, ReadFamilyMode(), SystemLight); }
        public static bool UsesSystem(string appLook) { return NormApp(appLook) == "hallow" && ReadFamilyMode() == "system"; }

        // dark colour in Theme.xaml / the code -> its light colour (spec palette). Accent colours are swapped separately.
        public static readonly Dictionary<string, string> Palette = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "#16121C", "#F6F4FA" },   // background
            { "#211B2A", "#FFFFFF" },   // panel
            { "#2E2639", "#EEEAF5" },   // panel 2
            { "#2A2333", "#EEEAF5" },   // button
            { "#241E2E", "#FFFFFF" },   // menus, tooltips, drop-downs
            { "#1A1522", "#FFFFFF" },   // text fields
            { "#3A3248", "#DAD3E6" },   // border / hover
            { "#55496A", "#B4A9C6" },   // hovered border
            { "#ECE8F4", "#1E1A26" },   // text
            { "#8F88A0", "#6B6478" },   // muted text
            { "#A98BE0", "#7A5CC0" },   // lilac
            { "#C8414B", "#F6D5D8" },   // pressed toggle (red tint)
            { "#E05A64", "#C2333F" },   // red
            { "#6ED28C", "#1E8E4F" },   // green
            { "#F0BE5A", "#A66A00" },   // amber
            { "#58566A", "#8F88A0" },   // switch off (3:1 against the panel)
            { "#CDCAD6", "#FFFFFF" },   // switch knob
            { "#FFFFFE", "#000000" },   // hover overlay (light on dark, dark on light)
            { "#5A5266", "#8F88A0" },   // offline dot
            { "#3A2E14", "#FBF0D9" },   // amber notice
            { "#0C0A10", "#E4DFEC" },   // behind the remote screen
        };
        public static string Map(string darkHex) { return Light ? Map2(darkHex) : darkHex; }
        public static string Map2(string darkHex) { string l; return Palette.TryGetValue(darkHex, out l) ? l : darkHex; }

        // the theme for this accent and mode. #F07821 in Theme.xaml marks the accent as text / thin lines (caret, focus border):
        // on light backgrounds that's the darkened accent.
        public static string BuildXaml(string xaml, string accent, bool light)
        {
            if (light) xaml = Regex.Replace(xaml, "#[0-9A-Fa-f]{6}\\b", m => { string l; return Palette.TryGetValue(m.Value, out l) ? l : m.Value; });
            xaml = Regex.Replace(xaml, "#F07821", light ? TextOn(accent, "#F6F4FA") : accent, RegexOptions.IgnoreCase);
            return Accent.ApplyTo(xaml, accent);
        }

        // contrast (WCAG)
        static double Lin(int v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        public static double Lum(string hex) { var c = Rgb(hex); return 0.2126 * Lin(c[0]) + 0.7152 * Lin(c[1]) + 0.0722 * Lin(c[2]); }
        public static double Contrast(string a, string b) { double x = Lum(a), y = Lum(b); return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05); }
        static int[] Rgb(string hex) { return new[] { int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber), int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber), int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber) }; }

        // the colour darkened in HSL until it reads as text (>= 4.5:1) on white and on the given background
        public static string TextOn(string hex, string bg = "#FFFFFF")
        {
            var c = Rgb(hex); double r = c[0] / 255.0, g = c[1] / 255.0, b = c[2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0, d = max - min;
            if (d > 0)
            {
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4; h /= 6;
            }
            var cur = hex.ToUpperInvariant();
            while (l > 0 && (Contrast(cur, "#FFFFFF") < 4.5 || Contrast(cur, bg) < 4.5)) { l = Math.Max(0, l - 0.01); cur = Hsl(h, s, l); }
            return cur;
        }
        static string Hsl(double h, double s, double l)
        {
            Func<double, double, double, double> f = (p, q, t) => { if (t < 0) t += 1; if (t > 1) t -= 1; return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p; };
            double r, g, b;
            if (s == 0) r = g = b = l;
            else { var q = l < 0.5 ? l * (1 + s) : l + s - l * s; var p = 2 * l - q; r = f(p, q, h + 1 / 3.0); g = f(p, q, h); b = f(p, q, h - 1 / 3.0); }
            return "#" + ((int)Math.Round(r * 255)).ToString("X2") + ((int)Math.Round(g * 255)).ToString("X2") + ((int)Math.Round(b * 255)).ToString("X2");
        }
    }
}

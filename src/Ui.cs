// Hallow Assist - small helpers for building WPF in code (no XAML compiler): colours, text, buttons, dark title bars.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HallowAssist
{
    public static class Ui
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
        // v1.11: while Dark is on (building the red bar), colours and styles come from the dark theme whatever the mode
        public static bool Dark;
        static ResourceDictionary darkDict; static string darkFor;
        static object Find(string key)
        {
            if (Dark && Look.Light)
            {
                if (darkDict == null || darkFor != Program.AccentHex) { darkDict = Program.ThemeDict(false); darkFor = Program.AccentHex; }
                return darkDict[key];
            }
            return Application.Current.FindResource(key);
        }
        static bool LightNow { get { return Look.Light && !Dark; } }
        public static Brush R(string key) { return (Brush)Find(key); }
        public static Style S(string key) { return (Style)Find(key); }
        // a colour written in the code as its dark value: the light one in light mode (Look.Palette)
        public static SolidColorBrush L(string darkHex) { return B(LightNow ? Look.Map(darkHex) : darkHex); }
        // the accent as text / outlines: darkened on light backgrounds so it reads (>= 4.5:1)
        public static Brush Accent { get { return B(LightNow ? Look.TextOn(Program.AccentHex, "#F6F4FA") : Program.AccentHex); } }
        public static Brush AccentFill { get { return B(Program.AccentHex); } }   // the real accent (drawing on the screen)
        // status colours; in light mode the spec colours, darkened where needed so text reads on the light backgrounds
        public static Brush Green { get { return Status("#6ED28C"); } }
        public static Brush Amber { get { return Status("#F0BE5A"); } }
        public static Brush Red { get { return Status("#E05A64"); } }
        static Brush Status(string dark) { return B(LightNow ? Look.TextOn(Look.Palette[dark], "#EEEAF5") : dark); }
        public static void TitleBar(Window w, bool dark)
        {
            try { var h = new WindowInteropHelper(w).Handle; if (h == IntPtr.Zero) return; int v = dark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref v, 4); } catch { }
        }

        // v1.11: an open window switched to the new accent / mode in place (no rebuild): styles swapped for the new theme's ones
        // (by resource key) and every colour the code set swapped for what the code would set now. Implicit styles and dynamic
        // resources follow by themselves when the app's resources change.
        public static string CodeColor(string src, bool light, string accent)
        {
            if (src == "ACCENT") return light ? Look.TextOn(accent, "#F6F4FA") : accent;
            if (src == "#6ED28C" || src == "#F0BE5A" || src == "#E05A64") return light ? Look.TextOn(Look.Palette[src], "#EEEAF5") : src;
            return light ? Look.Map2(src) : src;
        }
        public static Dictionary<string, string> ColorMap(bool oldLight, string oldAccent, bool newLight, string newAccent)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var srcs = new List<string> { "ACCENT" }; srcs.AddRange(Look.Palette.Keys);
            foreach (var s in srcs)
            {
                string a = CodeColor(s, oldLight, oldAccent), b = CodeColor(s, newLight, newAccent);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase) && !m.ContainsKey(a)) m[a] = b;
            }
            return m;
        }
        static readonly DependencyProperty[] brushProps = { System.Windows.Documents.TextElement.ForegroundProperty, System.Windows.Documents.TextElement.BackgroundProperty,
            Control.BackgroundProperty, Control.BorderBrushProperty, Border.BackgroundProperty, Border.BorderBrushProperty, Panel.BackgroundProperty,
            System.Windows.Shapes.Shape.FillProperty, System.Windows.Shapes.Shape.StrokeProperty };
        public static void Retheme(DependencyObject root, ResourceDictionary oldDict, ResourceDictionary newDict, Dictionary<string, string> colors)
        {
            var styleKey = new Dictionary<Style, object>();
            if (oldDict != null) foreach (var k in oldDict.Keys) { var st = oldDict[k] as Style; if (st != null && !styleKey.ContainsKey(st)) styleKey[st] = k; }
            Walk(root, styleKey, newDict, colors, 0);
        }
        static void Walk(DependencyObject d, Dictionary<Style, object> styleKey, ResourceDictionary newDict, Dictionary<string, string> colors, int depth)
        {
            if (d == null || depth > 200) return;
            var fe = d as FrameworkElement; object key;
            if (fe != null && fe.Style != null && styleKey.TryGetValue(fe.Style, out key) && newDict.Contains(key)) fe.Style = (Style)newDict[key];
            foreach (var dp in brushProps)
            {
                var b = d.ReadLocalValue(dp) as SolidColorBrush; if (b == null || b.Color.A != 255) continue;
                var hex = "#" + b.Color.R.ToString("X2") + b.Color.G.ToString("X2") + b.Color.B.ToString("X2"); string n;
                if (colors.TryGetValue(hex, out n)) try { d.SetValue(dp, B(n)); } catch { }
            }
            foreach (var c in LogicalTreeHelper.GetChildren(d)) Walk(c as DependencyObject, styleKey, newDict, colors, depth + 1);
            if (fe != null && fe.ContextMenu != null) Walk(fe.ContextMenu, styleKey, newDict, colors, depth + 1);
        }

        public static void Dress(Window w, string title, double width)
        {
            w.Title = title; w.Width = width; w.SizeToContent = SizeToContent.Height;
            w.Background = R("Bg"); w.Foreground = R("Text"); w.FontFamily = new FontFamily("Segoe UI"); w.FontSize = 12.5;
            try { w.Icon = AppIcon(); } catch { }
            bool keepDark = Dark;   // the red bar stays dark in both modes
            w.SourceInitialized += (s, e) => TitleBar(w, keepDark || !Look.Light);
        }
        static ImageSource icon;
        public static ImageSource AppIcon()
        {
            if (icon != null) return icon;
            var ico = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); icon.Freeze();
            return icon;
        }
        public static TextBlock T(string text, double size = 12.5, bool bold = false, Brush fg = null, double bottom = 0)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, bottom) };
            if (bold) t.FontWeight = FontWeights.SemiBold; if (fg != null) t.Foreground = fg;
            return t;
        }
        public static TextBlock Muted(string text, double size = 12, double bottom = 0) { return T(text, size, false, R("Muted"), bottom); }
        public static Button Btn(string label, Action click, bool accent = false, string tip = null)
        {
            var b = new Button { Content = label, Style = S(accent ? "Accent" : "Btn"), Margin = new Thickness(0, 0, 8, 0) };
            if (tip != null) b.ToolTip = tip;
            if (click != null) b.Click += (s, e) => click();
            return b;
        }
        public static StackPanel Row(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) p.Children.Add(i); return p; }
        public static Border Card(UIElement child, double pad = 18)
        {
            return new Border { Child = child, Background = R("Panel"), BorderBrush = R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(pad) };
        }
        public static FrameworkElement Header(string word, double size = 22)
        {
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            try { title.Children.Add(new Image { Source = AppIcon(), Width = size + 8, Height = size + 8, Margin = new Thickness(0, 0, 10, 0) }); } catch { }
            title.Children.Add(new TextBlock { Text = "HALLOW", FontSize = size, FontWeight = FontWeights.Bold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
            title.Children.Add(new TextBlock { Text = " " + word, FontSize = size, FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Center });
            return title;
        }
        public static CheckBox Switch(string text, bool on, Action<bool> changed)
        {
            var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Style = S("Switch") };
            c.Click += (s, e) => changed(c.IsChecked == true);
            return c;
        }
        public static PasswordBox PasswordField() { return new PasswordBox { Style = S("PasswordField"), Margin = new Thickness(0, 0, 0, 8) }; }
        public static TextBox Field(string text = "", double width = double.NaN) { var t = new TextBox { Text = text, Style = S("Field") }; if (!double.IsNaN(width)) t.Width = width; return t; }
        public static string FormatCode(string c) { c = Kdf.NormCode(c); return c.Length == 9 ? c.Substring(0, 3) + " " + c.Substring(3, 3) + " " + c.Substring(6) : c; }
    }
}

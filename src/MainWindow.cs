// Hallow Assist main window: "Get help" (show a code and password, nothing is shared until you click Allow) and
// "Help someone" (enter their code and password), plus settings: your relay, your name, unattended access, the Hallow links.
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HallowAssist
{
    public class MainWindow : Window
    {
        readonly Controller c;
        StackPanel getHelp, helpSomeone; TextBox code, pw, nameBox, relayBox; TextBlock helpStatus, relayStatus, unattendedInfo, state;
        public Button UpdateBtn;
        public bool Exiting;

        public MainWindow(Controller controller)
        {
            c = controller;
            Ui.Dress(this, "Hallow Assist", 860);
            ResizeMode = ResizeMode.CanMinimize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Build();
            Closing += (s, e) => { if (!Exiting && !Snap.On) { e.Cancel = true; c.MainClosing(); } };
            AllowDrop = false;
        }

        public void Build()
        {
            var sp = new StackPanel { Margin = new Thickness(26, 16, 26, 18) };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            UpdateBtn = new Button { Style = Ui.S("Accent"), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
            UpdateBtn.Click += (s, e) => c.InstallUpdate();
            DockPanel.SetDock(UpdateBtn, Dock.Right); top.Children.Add(UpdateBtn);
            top.Children.Add(Ui.Header("ASSIST"));
            sp.Children.Add(top);
            if (AssistSettings.WsUri(c.S.RelayUrl) == null)
            {
                var warn = new Border { Background = Ui.B("#3A2E14"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 12) };
                warn.Child = Ui.T("First, set the relay address below (Settings). Hallow Assist connects through a relay that you or your helper run - see NAS-DEPLOY.md on GitHub.", 12.5, false, Ui.Amber);
                sp.Children.Add(warn);
            }
            var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            getHelp = new StackPanel(); helpSomeone = new StackPanel();
            var c1 = Ui.Card(getHelp); var c2 = Ui.Card(helpSomeone); Grid.SetColumn(c2, 2);
            grid.Children.Add(c1); grid.Children.Add(c2);
            sp.Children.Add(grid);
            state = Ui.T("", 12.5, false, null, 0); state.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(state);
            sp.Children.Add(BuildSettings());
            sp.Children.Add(Ui.Muted("Hallow Assist " + Program.Version + "  -  everything between the two PCs is end-to-end encrypted; your relay can't see screens, keys or chat.  -  icons: Font Awesome Free (CC BY 4.0)", 11, 0));
            Content = sp;
            RefreshGetHelp(); RefreshHelpSomeone();
        }

        static FrameworkElement CardTitle(string icon, string text)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var i = Icons.Make(icon, 22, Ui.Accent); i.Margin = new Thickness(0, 0, 10, 0); r.Children.Add(i);
            r.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            return r;
        }

        public void RefreshGetHelp()
        {
            if (getHelp == null) return;
            getHelp.Children.Clear();
            getHelp.Children.Add(CardTitle("hand-holding-heart", "Get help"));
            var h = c.Host;
            if (h == null || h.State == HostState.Ended || h.Unattended)
            {
                getHelp.Children.Add(Ui.Muted("Someone you trust sees your screen - and, if you let them, uses your mouse and keyboard. They get a code and password from you, and nothing is shared until you click Allow.", 12.5, 12));
                var b = Ui.Btn("Get help", () => c.GetHelp(), true); b.HorizontalAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(18, 8, 18, 8);
                b.IsEnabled = AssistSettings.WsUri(c.S.RelayUrl) != null;
                getHelp.Children.Add(b);
                if (h != null && h.State == HostState.Ended && !h.Unattended && h.EndReason != null) getHelp.Children.Add(Ui.T(h.EndReason, 12, false, Ui.Amber, 0));
                if (h != null && h.Unattended && h.State != HostState.Ended) getHelp.Children.Add(Ui.Muted("Unattended access is on: this PC is waiting for you (see Settings).", 12, 0));
                return;
            }
            if (h.State == HostState.Starting) { getHelp.Children.Add(Ui.Muted("Connecting to the relay...", 13)); getHelp.Children.Add(Ui.Btn("Cancel", () => c.StopHost("Cancelled."))); return; }
            if (h.State == HostState.Connected)
            {
                getHelp.Children.Add(Ui.T(h.HelperName + " is connected" + (h.Control ? " and can use your mouse and keyboard." : " and can see your screen."), 14, true, null, 6));
                getHelp.Children.Add(Ui.Muted("Safety code " + h.Safety + ". The red bar at the top of your screen shows this too. Ctrl+Alt+End disconnects at once.", 12.5, 10));
                if (h.RecordingAsked) getHelp.Children.Add(Ui.T("\u25cf " + h.HelperName + " is recording this session on their PC - Stop recording is on the red bar.", 12.5, true, Ui.Red, 10));   // v1.5
                var db = Ui.Btn("Disconnect", () => c.StopHost("You disconnected."), true); db.HorizontalAlignment = HorizontalAlignment.Left; getHelp.Children.Add(db);
                return;
            }
            getHelp.Children.Add(Ui.Muted("Tell the person helping you these two (by phone or in person):", 12.5, 6));
            var codeRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            codeRow.Children.Add(Ui.Muted("Code", 11.5));
            codeRow.Children.Add(new TextBlock { Text = h.Code == null ? "..." : Ui.FormatCode(h.Code), FontSize = 34, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"), Foreground = Ui.Accent });
            codeRow.Children.Add(Ui.Muted("Password", 11.5));
            codeRow.Children.Add(new TextBlock { Text = h.Password ?? "...", FontSize = 30, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas") });
            getHelp.Children.Add(codeRow);
            var st = h.State == HostState.Checking ? "Someone is connecting - checking the password..." : h.State == HostState.Asking ? h.HelperName + " is asking to connect - answer the question on your screen." :
                h.WrongPasswords > 0 ? "Someone typed a wrong password (" + h.WrongPasswords + " of 3). Still waiting." : "Waiting for your helper. The code stops working if nobody uses it within 10 minutes.";
            getHelp.Children.Add(Ui.T(st, 12.5, false, h.WrongPasswords > 0 ? Ui.Amber : Ui.R("Muted"), 10));
            getHelp.Children.Add(Ui.Row(Ui.Btn("Copy code and password", () => { try { Clipboard.SetText("Hallow Assist code " + Ui.FormatCode(h.Code) + "  password " + h.Password); state.Text = "Copied - paste it into a message only to the person you trust."; } catch { } }),
                Ui.Btn("Cancel", () => c.StopHost("Cancelled."))));
        }

        public void RefreshHelpSomeone()
        {
            if (helpSomeone == null) return;
            var keepCode = code != null ? code.Text : "";
            helpSomeone.Children.Clear();
            helpSomeone.Children.Add(CardTitle("display", "Help someone"));
            var v = c.Viewer;
            if (v != null && v.State != ViewerState.Ended)
            {
                helpSomeone.Children.Add(Ui.T(v.State == ViewerState.Connected ? "You're connected - their screen is in its own window." : v.Status, 13, true, null, 8));
                helpSomeone.Children.Add(Ui.Row(Ui.Btn("Show their screen", () => c.ShowViewer()), Ui.Btn("Disconnect", () => v.End("You ended the session.", true), true)));
                // v1.1: one of your own PCs (not a one-time password) can be kept in Your PCs
                if (v.State == ViewerState.Connected && !v.OneTimePassword && c.S.FindPc(v.Code) == null && c.S.Pcs.Count < AssistSettings.MaxPcs)
                {
                    var save = Ui.Btn("Save this PC to Your PCs", () =>
                    {
                        var d = new SavePcDialog(v.Code, false) { Owner = this };
                        if (d.ShowDialog() == true) { c.SavePc(d.PcName, v.Code, d.Remember ? v.PasswordKey : null, v.HostUnattended ? v.HostMac : null); Say("Saved to Your PCs."); }
                    });
                    save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new Thickness(0, 10, 0, 0);
                    helpSomeone.Children.Add(save);
                }
                else
                {
                    // already one of Your PCs, password typed this time: offer to remember it (only after it worked)
                    var known = c.S.FindPc(v.Code);
                    if (v.State == ViewerState.Connected && known != null && known.Key == null && known.KeysItem == null && !v.UsedSavedKey && !v.OneTimePassword && v.PasswordKey != null)
                    {
                        var rem = Ui.Btn("Remember the password for " + known.Name, () =>
                        {
                            if (MessageBox.Show(this, "Remember the password for " + known.Name + " on this PC?\n\nIt's kept as a key made from it, encrypted for your Windows account - but anyone who can sign in as you here could then connect to " + known.Name + ".",
                                "Hallow Assist", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                            c.SavePc(known.Name, known.Code, v.PasswordKey); Say("Password remembered for " + known.Name + ".");
                        });
                        rem.HorizontalAlignment = HorizontalAlignment.Left; rem.Margin = new Thickness(0, 10, 0, 0);
                        helpSomeone.Children.Add(rem);
                    }
                }
                return;
            }
            AddYourPcs(v);
            helpSomeone.Children.Add(Ui.Muted("Ask them to open Hallow Assist and click Get help, then type what it shows. Or use any browser (phone, tablet, TV) at your relay's address.", 12.5, 10));
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            code = Ui.Field(keepCode); code.FontSize = 16; code.MaxLength = 13; pw = Ui.Field(); pw.FontSize = 16; pw.CharacterCasing = CharacterCasing.Normal; nameBox = Ui.Field(c.S.Name); nameBox.MaxLength = 40;
            AddRow(g, 0, "Their code", code); AddRow(g, 1, "Password", pw); AddRow(g, 2, "Your name", nameBox);
            helpSomeone.Children.Add(g);
            helpStatus = Ui.T(v != null && v.State == ViewerState.Ended ? v.EndReason : "", 12, false, Ui.Amber, 6);
            helpSomeone.Children.Add(helpStatus);
            var go = Ui.Btn("Connect", () =>
            {
                var cd = Kdf.NormCode(code.Text);
                if (cd.Length != 9) { helpStatus.Text = "The code has 9 digits."; return; }
                if (pw.Text.Trim().Length == 0) { helpStatus.Text = "Type the password they read out to you."; return; }
                if (nameBox.Text.Trim().Length == 0) { helpStatus.Text = "Type your name - they see it before they say yes."; return; }
                c.S.Name = nameBox.Text.Trim(); c.S.Save();
                c.Connect(cd, pw.Text, c.S.Name);
            }, true);
            go.IsDefault = true; go.HorizontalAlignment = HorizontalAlignment.Left; go.Padding = new Thickness(18, 8, 18, 8);
            go.IsEnabled = AssistSettings.WsUri(c.S.RelayUrl) != null;
            var add = Ui.Btn("Add one of your PCs", AddPc); add.IsEnabled = c.S.Pcs.Count < AssistSettings.MaxPcs;
            var buttons = Ui.Row(go, add); buttons.HorizontalAlignment = HorizontalAlignment.Left;
            helpSomeone.Children.Add(buttons);
            if (focusPassword) { focusPassword = false; Dispatcher.BeginInvoke(new Action(() => pw.Focus()), System.Windows.Threading.DispatcherPriority.Input); }
        }

        // ---- v1.1: Your PCs (own PCs with unattended access): one-click Connect, newest first
        bool focusPassword;
        readonly System.Collections.Generic.Dictionary<string, System.Windows.Shapes.Ellipse> dots = new System.Collections.Generic.Dictionary<string, System.Windows.Shapes.Ellipse>();
        void PaintDot(System.Windows.Shapes.Ellipse d, string code)
        {
            string st; c.PcStatus.TryGetValue(code, out st);
            d.Fill = st == "online" ? Ui.Green : st == "busy" ? Ui.Amber : st == "offline" ? Ui.B("#5A5266") : Brushes.Transparent;
            d.Stroke = st == null ? Ui.B("#5A5266") : null; d.StrokeThickness = 1.2;
            d.ToolTip = st == "online" ? "Online - waiting for you" : st == "busy" ? "Online - someone is connected to it" : st == "offline" ? "Offline (switched off, asleep, or Hallow Assist isn't running there)" : "Not checked yet";
        }
        public void ShowPcStatus() { foreach (var kv in dots) PaintDot(kv.Value, kv.Key); }
        void AddYourPcs(ViewerSession last)
        {
            dots.Clear();
            if (c.WaitingForRestart)
            {
                var wr = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                var stop = Ui.Btn("Stop waiting", () => { c.CancelRestartWait(); RefreshHelpSomeone(); }); DockPanel.SetDock(stop, Dock.Right); wr.Children.Add(stop);
                var wt = Ui.T("Waiting for your PC to restart - it reconnects by itself.", 12.5, false, Ui.Amber); wt.VerticalAlignment = VerticalAlignment.Center; wt.TextWrapping = TextWrapping.Wrap; wr.Children.Add(wt);
                helpSomeone.Children.Add(wr);
            }
            if (c.S.Pcs.Count == 0) return;
            helpSomeone.Children.Add(Ui.T("Your PCs", 13, true, null, 4));
            bool canGo = AssistSettings.WsUri(c.S.RelayUrl) != null;
            foreach (var pc in c.S.Pcs.OrderByDescending(p => p.LastUsed ?? "").ThenBy(p => p.Name))
            {
                var p = pc;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                // v1.5: Password... (typed, remembered, Hallow Keys) and Forget live in a small menu, so the row stays readable
                var more = Ui.Btn("\u22ef", null); more.ToolTip = "Password, Forget"; more.Margin = new Thickness(6, 0, 0, 0); more.VerticalAlignment = VerticalAlignment.Center;
                var menu = new ContextMenu();
                var miPw = new MenuItem { Header = "Password..." };
                miPw.Click += (s, e) => { var d = new PcPasswordDialog(p) { Owner = this }; if (d.ShowDialog() == true) { c.SetPcPassword(p.Code, d.Mode, d.Item); Say(d.Mode == "keys" ? p.Name + " gets its password from Hallow Keys (item \u201c" + d.Item + "\u201d)." : "Saved."); } };
                var miForget = new MenuItem { Header = "Forget" };
                miForget.Click += (s, e) => { if (MessageBox.Show(this, "Remove " + p.Name + " from Your PCs?" + (p.Key != null ? " Its remembered password goes too." : ""), "Hallow Assist", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) c.ForgetPc(p.Code); };
                menu.Items.Add(miPw); menu.Items.Add(miForget);
                more.ContextMenu = menu; more.Click += (s, e) => { menu.PlacementTarget = more; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; menu.IsOpen = true; };
                DockPanel.SetDock(more, Dock.Right); row.Children.Add(more);
                var connect = Ui.Btn("Connect", () =>
                {
                    if (c.ConnectSaved(p)) return;
                    code.Text = Ui.FormatCode(p.Code); pw.Clear(); focusPassword = true;
                    helpStatus.Text = "Type the unattended password for " + p.Name + "."; helpStatus.Foreground = Ui.R("Muted");
                    pw.Focus();
                }, true);
                connect.IsEnabled = canGo && !c.AskingKeys; connect.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(connect, Dock.Right); row.Children.Add(connect);
                // v1.4: Wake-on-LAN, once this PC's network adapter is known (learned in an unattended session)
                if (Wol.ParseMac(p.Mac) != null)
                {
                    string st; c.PcStatus.TryGetValue(p.Code, out st);
                    var waking = c.Waking(p.Code);
                    var wake = Ui.Btn(waking ? "Stop waiting" : "Wake", () => { if (c.Waking(p.Code)) c.CancelWake(); else c.Wake(p); });
                    wake.ToolTip = "Wake " + p.Name + " from sleep or off (Wake-on-LAN, network adapter " + Wol.ShowMac(p.Mac) + "). Only works on the same network as this PC, with Wake-on-LAN turned on in its BIOS and its network adapter's settings.";
                    wake.IsEnabled = waking || st != "online";
                    wake.Margin = new Thickness(0, 0, 6, 0); wake.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(wake, Dock.Right); row.Children.Add(wake);
                }
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var nameText = Ui.T(p.Name, 13, true); nameText.TextTrimming = TextTrimming.CharacterEllipsis;
                // v1.2: online dot (green = waiting for you, amber = someone is connected, grey = offline)
                var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                dots[p.Code] = dot; PaintDot(dot, p.Code);
                var nameRow = new DockPanel(); DockPanel.SetDock(dot, Dock.Left); nameRow.Children.Add(dot); nameRow.Children.Add(nameText);
                info.Children.Add(nameRow);
                var detail = Ui.Muted(Ui.FormatCode(p.Code) + (p.KeysItem != null ? "  -  from Hallow Keys: " + p.KeysItem : p.Key != null ? "  -  remembered" : "  -  type the password"), 11.5); detail.TextTrimming = TextTrimming.CharacterEllipsis; info.Children.Add(detail);
                info.ToolTip = p.KeysItem != null ? "Password from Hallow Keys: " + p.KeysItem + " (you approve it in Keys each time; it's never stored here)." : p.Key != null ? "The password is remembered on this PC (as an encrypted key)." : "You type the password each time.";
                row.Children.Add(info);
                if (c.SelectedPc == p.Code)   // v1.10: picked from your phone ("connect <name>") - outlined until you connect
                {
                    row.Margin = new Thickness(0);
                    helpSomeone.Children.Add(new Border { Child = row, BorderBrush = Ui.Accent, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 6, 5), Margin = new Thickness(-9, -2, -7, 6) });
                }
                else helpSomeone.Children.Add(row);
            }
            var sep = new Border { Height = 1, Background = Ui.R("Line"), Margin = new Thickness(0, 4, 0, 10) };
            helpSomeone.Children.Add(sep);
        }
        // v1.10 "connect <name>" (Hallow Remote / Hub): that PC outlined, its code in the box - Connect is still yours to click
        public void SelectPc(SavedPc p)
        {
            RefreshHelpSomeone();
            if (code == null || pw == null || helpStatus == null || !helpSomeone.Children.Contains(helpStatus)) return;
            code.Text = Ui.FormatCode(p.Code); pw.Clear();
            helpStatus.Text = PcShare.SelectedText(p); helpStatus.Foreground = Ui.R("Muted");
        }
        void AddPc()
        {
            var d = new SavePcDialog(null, true) { Owner = this };
            if (d.ShowDialog() != true) return;
            if (c.S.FindPc(d.Code) != null && MessageBox.Show(this, "That code is already in Your PCs. Replace it?", "Hallow Assist", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            c.SavePc(d.PcName, d.Code, d.Remember && !string.IsNullOrEmpty(d.Password) ? Kdf.PasswordKey(d.Password, d.Code) : null);
            Say("Added to Your PCs.");
        }
        static void AddRow(Grid g, int r, string label, FrameworkElement f)
        {
            var l = Ui.Muted(label, 12.5); l.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(l, r); g.Children.Add(l);
            f.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(f, r); Grid.SetColumn(f, 1); g.Children.Add(f);
        }

        UIElement BuildSettings()
        {
            var sp = new StackPanel();
            sp.Children.Add(Ui.T("Settings", 15, true, null, 6)); ((FrameworkElement)sp.Children[0]).Margin = new Thickness(0, 16, 0, 6);
            var rr = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var lbl = Ui.Muted("Relay address", 12.5); lbl.Width = 110; lbl.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(lbl, Dock.Left); rr.Children.Add(lbl);
            var save = Ui.Btn("Save", SaveRelay); save.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(save, Dock.Right); rr.Children.Add(save);
            relayBox = Ui.Field(c.S.RelayUrl); rr.Children.Add(relayBox);
            sp.Children.Add(rr);
            relayStatus = Ui.Muted("Both people use the same relay, e.g. https://assist.example.com. The helper can also open that address in any browser.", 11.5, 8);
            relayStatus.Margin = new Thickness(110, 0, 0, 8);
            sp.Children.Add(relayStatus);
            sp.Children.Add(Ui.Switch("Use the Hallow icon (when Hallow Hub runs, Hallow Assist lives in its tray menu)", c.S.UseHallowIcon, v => { c.S.UseHallowIcon = v; c.S.Save(); c.CheckHost(); }));
            sp.Children.Add(Ui.Switch("Use the Hallow accent (the colour picked in Hallow Toolkit)", c.S.UseHallowAccent, v => { c.S.UseHallowAccent = v; c.S.Save(); c.ApplyAccent(); }));
            // unattended access - off by default
            var ub = new StackPanel();
            ub.Children.Add(Ui.Switch("Unattended access to this PC - only for your own PCs (needs its own strong password and Start with Windows)", c.S.Unattended, v => c.SetUnattended(v)));
            unattendedInfo = Ui.Muted(UnattendedText(), 11.5, 0); unattendedInfo.Margin = new Thickness(52, 0, 0, 4);
            ub.Children.Add(unattendedInfo);
            if (c.S.Unattended) ub.Children.Add(Ui.Switch("Share this PC's sound in unattended sessions (you can still stop it on the red bar) - off by default", c.S.SoundUnattended, v => { c.S.SoundUnattended = v; c.S.Save(); }));
            if (c.S.Unattended) ub.Children.Add(Ui.Switch(OwnFiles.SettingText, c.S.AskOwnFiles, v => c.SetAskOwnFiles(v)));   // v1.7
            if (c.S.Unattended) ub.Children.Add(Ui.Switch(RemoteClip.SettingText, !c.S.ClipRemoteOff, v => c.SetClipRemote(v)));   // v1.9
            sp.Children.Add(ub);
            sp.Children.Add(Ui.Switch("Connect directly when both PCs are on the same network - faster and smoother (the helper's PC may ask once to allow Hallow Assist on private networks)", !c.S.DirectOff, v => { c.S.DirectOff = !v; c.S.Save(); }));
            // v1.5: where received files go
            var rf = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var rl = Ui.Muted("Received files", 12.5); rl.Width = 110; rl.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(rl, Dock.Left); rf.Children.Add(rl);
            var reset = Ui.Btn("Downloads", () => { c.S.ReceiveFolder = null; c.S.Save(); Build(); }); reset.ToolTip = "Back to Downloads\\Hallow Assist"; reset.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(reset, Dock.Right); rf.Children.Add(reset);
            var pick = Ui.Btn("Change...", () =>
            {
                using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Where should files you receive go?", SelectedPath = c.S.ReceiveDir() })
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK && System.IO.Directory.Exists(d.SelectedPath)) { c.S.ReceiveFolder = d.SelectedPath; c.S.Save(); Build(); }
            });
            pick.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(pick, Dock.Right); rf.Children.Add(pick);
            var rp = Ui.T(Snap.On ? @"C:\Users\you\Downloads\Hallow Assist" : c.S.ReceiveDir(), 12.5); rp.VerticalAlignment = VerticalAlignment.Center; rp.TextTrimming = TextTrimming.CharacterEllipsis; rf.Children.Add(rp);   // --snap: never a real path
            sp.Children.Add(rf);
            var row = Ui.Row(Ui.Btn("Check for updates", () => c.CheckForUpdate(true)), Ui.Btn("Connection history", () => c.ShowHistory()), Ui.Btn("Open received files", () => OpenFolder(c.S.ReceiveDir())), Ui.Btn("Open recordings", () => OpenFolder(SessionRecorder.DefaultFolder())));
            row.Margin = new Thickness(0, 8, 0, 8); sp.Children.Add(row);
            return sp;
        }
        static void OpenFolder(string dir) { try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\""); } catch { } }
        string UnattendedText()
        {
            if (!c.S.Unattended) return "Off. Nobody can connect to this PC unless you click Get help and then Allow.";
            var st = c.Host != null && c.Host.Unattended ? (c.Host.State == HostState.Connected ? "someone is connected now" : c.Host.State == HostState.Ended ? "not connected to the relay right now" : "waiting for you") : "starting";
            return "On (" + st + "). This PC's code: " + Ui.FormatCode(c.S.UnattendedCode) + " - use it with your unattended password. Starts with Windows.";
        }
        void SaveRelay()
        {
            var t = relayBox.Text.Trim();
            if (t.Length > 0 && AssistSettings.WsUri(t) == null) { relayStatus.Text = "That doesn't look like a relay address. Use https://... (plain http only for an address on this PC or your home network)."; relayStatus.Foreground = Ui.Amber; return; }
            c.S.RelayUrl = t; c.S.Save();
            relayStatus.Text = t.Length == 0 ? "No relay set." : "Saved. Checking " + AssistSettings.WebUrl(t) + "healthz ..."; relayStatus.Foreground = Ui.R("Muted");
            Build();
            c.CheckRelay(r => Dispatcher.BeginInvoke(new Action(() => { relayStatus.Text = r; })));
        }
        public void Say(string text, Brush color = null) { if (state != null) { state.Text = text; state.Foreground = color ?? Ui.R("Muted"); } }
        public void RefreshAll() { RefreshGetHelp(); RefreshHelpSomeone(); if (unattendedInfo != null) unattendedInfo.Text = UnattendedText(); }
    }
}

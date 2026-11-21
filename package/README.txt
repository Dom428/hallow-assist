HALLOW ASSIST - remote help for friends and family

See and control a PC with their OK, from your PC, phone or TV browser.

HOW TO INSTALL
1. Right-click the zip > Properties > tick "Unblock" > OK
2. Right-click the zip > Extract All
3. Double-click INSTALL.bat (no admin needed)
4. In Hallow Assist > Settings, set your relay address (both people use the same one).
   The relay is the "server" folder in this zip - see NAS-DEPLOY.md
   (https://github.com/Dom428/hallow-assist/blob/main/NAS-DEPLOY.md).

GETTING HELP
- Click Get help. Read the code and password to the person helping you.
- When they connect you see their name and a safety code: click Allow or Deny.
  Nothing is shared before Allow.
- While they're connected a red bar stays at the top of your screen.
  Disconnect there, press Ctrl+Alt+End, or close Hallow Assist to end it.

HELPING SOMEONE
- Type their code and password under Help someone - or open your relay's address in
  any browser (phone, tablet, TV, another PC).
- Mouse and keyboard (if they allowed it), their monitors, chat, copied text and copied
  files, files both ways (they're asked first), picture quality, recording on your PC.
- Ctrl+Alt+Del and admin (UAC) prompts can't be used remotely: they click those themselves.

UNATTENDED ACCESS (your own PCs only)
- Off by default. Settings > Unattended access needs its own strong password and
  Start with Windows. The red bar still shows while you're connected.
- Sound isn't shared in unattended sessions unless you turn that on in Settings.
- Copied files are shared in unattended sessions without a question (and noted in Connection
  history) unless you turn on "Ask before my own PCs share files in unattended sessions".

PRIVACY
- End-to-end encrypted: your relay passes sealed messages along and can't see screens,
  keys, chat or files. Crash reports stay on this PC. Nothing else is sent anywhere.

WHAT'S NEW IN 1.8
- New app icon: a hand holding a heart, in the Hallow family style (the relay's web page too).

WHAT'S NEW IN 1.7
- Your own PCs (unattended sessions) share their copied files without a question: "Copy their files
  here" sends them right away (you're still asked before saving), the PC's Connection history says
  "copied N files from this PC: <names>" with the time, and its red bar briefly says "Files copied by
  <helper>". Sessions started with Get help still ask, as in 1.6. To keep the question on your own
  PCs, turn on Settings > "Ask before my own PCs share files in unattended sessions" (off by default).

WHAT'S NEW IN 1.6
- "Copy their files here" now asks the person being helped first: their PC shows which files and how
  big, with Allow / Deny (Deny if nobody answers in 60 s; Ctrl+Alt+End still ends the session).
  Nothing leaves their PC before Allow. Both PCs need 1.6 for this - a 1.6 helper says
  "They need Hallow Assist 1.6 to share files this way" to an older PC.

WHAT'S NEW IN 1.5
- Your PCs can get their password from Hallow Keys (2.6 or newer): ... > Password... > "Password from
  Hallow Keys" and the item's name. You approve each use in Keys; the password is used once and wiped,
  never stored or logged. Turn on Keys > Settings > Developer tools > command line first.
- Copied files both ways while clipboard sharing is on: "Send the files I copied" / "Copy their files here"
  in the viewer, "Send copied files" on the red bar. One question for all the files; afterwards they're
  on the receiver's clipboard too (Ctrl+V in any folder). Same safety rules as other files.
- Session recording on the helper's PC (off until you click Record): Videos\Hallow Assist, MP4 with
  Hallow Clips' FFmpeg if it's there, otherwise AVI. The person helped sees "Recording" on the red bar
  the whole time and can press Stop recording; it never starts without that notice.
- Settings: pick where received files go.

WHAT'S NEW IN 1.4
- Wake-on-LAN for Your PCs: after one unattended session your app knows that PC's network adapter, and
  Wake (in Your PCs) wakes it - same network only, with Wake-on-LAN turned on in its BIOS / network adapter.
- Session sound, off on both sides: the helper turns on Sound, you click Share sound on the red bar
  (it then says "Sound shared"). Only your speakers' sound, never the microphone. App viewer only.
- Direct connection fixes: only switches after the helper's app proves itself, prefers your real home
  network over virtual adapters, and a dropped direct connection carries on through the relay.

WHAT'S NEW IN 1.3
- Direct connection on the same network: faster and smoother, same encryption. The helper's PC
  may ask once to allow Hallow Assist on private networks. Settings has a switch to turn it off.

WHAT'S NEW IN 1.2
- Online dots in Your PCs, a connection history on the PC being helped (Settings),
  drawing on their screen (app and browser), and Restart their PC for your own PCs -
  Hallow Assist connects again by itself when it's back. Update your relay to 1.2 for the dots.

WHAT'S NEW IN 1.1
- Your PCs: save your own PCs (unattended access) and connect with one click.
- Unattended access really comes back after its 15-minute pause.
- Received files are marked as from the internet, and the prompt warns about programs and scripts.
- Safer file names and picture sizes; the relay no longer drops you when a slow helper leaves.

CREDITS
Icons: Font Awesome Free 7.3.1 by Fonticons, Inc. (https://fontawesome.com), licensed CC BY 4.0 - see FontAwesome-LICENSE.txt.

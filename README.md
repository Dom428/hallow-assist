# Hallow Assist

**Remote help for friends and family: see and control a PC with their OK, from your PC, phone or TV browser.**

Part of the Hallow family of Windows apps. Like TeamViewer or AnyDesk, but it runs through **your own relay** (a tiny
server on your NAS or any Docker box, see [NAS-DEPLOY.md](NAS-DEPLOY.md)) and everything between the two ends is
end-to-end encrypted.

## How it works

- **Get help** (the person being helped): click *Get help* - Hallow Assist shows a 9-digit code and a 6-character
  one-time password. They read both to you. When you connect, they see a clear question with your name and a 4-digit
  safety code: **Allow** or **Deny** (and whether you may use the mouse and keyboard, and share copied text).
  Nothing is shared before Allow.
- While you're connected, a red bar stays on top of their screen: "*Dom can see your screen and control it*", with
  **Stop control**, **Chat**, **Send a file** and **Disconnect**. It can't be minimized or closed away (closing it
  disconnects). **Ctrl+Alt+End** disconnects at once, and so does closing the app.
- **Help someone** (you): type their code and password in Hallow Assist, or open your relay's address in **any modern
  browser** - phone, tablet, TV or another PC. You see their screen (switch between monitors), and if they allowed it you
  use their mouse and keyboard. Chat, copied text both ways (when they turn it on), files both ways (the other side is
  always asked first), picture quality and connection stats.
- In the browser on a phone or tablet: tap = click, long press = right click, one finger drag = drag, two fingers =
  scroll, pinch = zoom, an on-screen keyboard with Esc/Tab/Ctrl/Alt/Win/arrow keys. Works in landscape.

## What it won't do (on purpose)

- No stealth mode, no hidden install, no starting a session by itself - ever. Every session starts with the person
  clicking Get help, and the bar is always visible while someone is connected.
- **Unattended access** (connect without anyone clicking Allow) is only for **your own PCs**: off by default; turning it on
  needs its own strong password (never stored - only a key made from it, encrypted for your Windows account) and Start with
  Windows. The bar still shows while you're connected, and Ctrl+Alt+End still works.
- **Ctrl+Alt+Del** can't be sent - Windows only accepts it from the PC's own keyboard. The button is there and says so.
- **Admin prompts (UAC) and windows that run as administrator** can't be clicked from a normal app. Hallow Assist notices
  and tells you: "They need to click the admin prompt themselves". (An admin service is on the roadmap.)

## Security

- The two ends agree a key with **ECDH P-256** (Windows CNG / the browser's WebCrypto), bound to the one-time password and
  the session code (**PBKDF2-SHA256** with 100,000 rounds, then **HKDF-SHA256**), and prove it to each other with a
  **key-confirmation** step - a wrong password fails before anything else happens, and the relay can't read or change
  anything. Every message after that is **AES-256-GCM** with a counter; changed, replayed or reordered messages end the
  session. Both screens show the same **4-digit safety code** you can compare by phone.
- The relay pairs the two ends by code and passes sealed messages along. Codes are random, expire after 10 minutes if
  unused, wrong codes are rate-limited per visitor, there's one helper per session, a maximum message size and idle
  timeouts. It never logs payloads, codes or passwords, and stores nothing.
- Three wrong passwords close a code; five wrong unattended passwords pause unattended access for 15 minutes.
- Limits worth knowing: the password check is a password-bound key agreement, not a full PAKE - comparing the safety code
  is the strongest check against a relay that tries to sit in the middle. A dishonest relay that sits in the middle can
  also try passwords offline against what it saw, so keep the unattended password long (a passphrase of 4+ random
  words) and always compare the safety code. Your relay operator (you) can see *that* two
  people are connected, from which addresses, and for how long.

## What's new

- **1.4** - **Wake-on-LAN for Your PCs**: when you connect to one of your own PCs (unattended access), it tells your app its
  network adapter (inside the encrypted session, and only in unattended sessions). Then **Wake** next to it in Your PCs
  sends the standard "magic packet" across your network and waits for its online dot to turn green. It only works on the
  same network as that PC, with Wake-on-LAN turned on in its BIOS and its network adapter's settings. **Session sound**
  (optional, off on both sides): the helper turns on **Sound** in the viewer, the person being helped clicks **Share
  sound** on the red bar (which then says "Sound shared") - only then does the PC's sound (what its speakers play, never
  the microphone) go to the helper, end-to-end encrypted like everything else (16 kHz, IMA ADPCM - about 64 kbit/s). Your
  own PCs can share it in unattended sessions if you turn that on in Settings. App viewer only for now (not the browser).
  **Direct connection fixes** from a review of 1.3: the PC being helped only switches to a direct connection after the
  helper's app has proved it holds this session's one-time token (a different device that happens to answer on that
  address - another network using the same private addresses - is never used); the real home-network address is offered
  first (virtual adapters from Hyper-V, WSL, Docker, VirtualBox or VPNs no longer crowd it out); only addresses on the
  PC's own subnets and ports from 1024 are tried; a hand-over that can't finish stays on the relay instead of risking the
  message order; and if the direct connection drops mid-session, the session carries on through the relay instead of
  ending. "Couldn't connect directly" is no longer shown for every session across networks - it's in the connection
  details (hover the stats in the viewer).
- **1.3** - **Direct connection** when both PCs are on the same network: after the usual secure start through your relay,
  the picture, input, chat, files and drawing go straight between the two PCs - faster and smoother. The PC being helped
  connects *out* to the helper's PC (so it never shows a firewall question; the helper's PC may ask once to allow
  Hallow Assist on private networks). Same end-to-end encryption; only the path changes. Anything that doesn't work
  stays on the relay. The viewer shows "direct" or "via relay". Switch it off in Settings. App to app only (a browser
  can't take a direct connection).
- **1.2** - **Online dots** in Your PCs (green = waiting for you, amber = someone is connected, grey = offline; your relay
  only ever answers this for unattended codes). **Connection history** on the PC being helped (Settings > Connection
  history: who, when, how long, whether they used the mouse and keyboard, files received). **Draw on their screen** -
  from the app or any browser, lines fade after a few seconds, also in view-only sessions. **Restart their PC** for your
  own PCs (unattended access): Windows restarts in 10 seconds and Hallow Assist connects again by itself when it's back
  (after the sign-in, if Windows asks for one). Needs relay 1.2 for the online dots.
- **1.1** - **Your PCs**: save your own PCs (the ones with unattended access) under Help someone and connect with one
  click. Their password is only remembered if you tick it - as a key made from it, encrypted for your Windows account,
  never the password itself - and a remembered key that stops working is forgotten. Also security and reliability fixes from a review: unattended access really comes back after its 15-minute pause
  (before, it stayed off until the app restarted); only wrong passwords in a row count towards that pause; files you
  receive are marked as from the internet (Windows SmartScreen warns before running them) and the file prompt says when
  a file is a program, script or shortcut; every reserved Windows device name is refused as a file name; a frame claiming
  a giant picture is refused instead of running the helper's app out of memory. Relay 1.1: when a slow helper leaves
  mid-picture the relay goes on listening to the person being helped (before, it could drop them about a minute later).
- **1.0** - First version.

## Install

1. Download the zip from [Releases](https://github.com/Dom428/hallow-assist/releases/latest), right-click > Properties >
   Unblock, Extract All.
2. Double-click **INSTALL.bat** (no admin needed). It installs to `%LOCALAPPDATA%\Programs\Hallow Assist` with Start menu
   and Desktop shortcuts.
3. Set your relay address in Settings (both people use the same relay). The `server` folder in the zip is the relay -
   see [NAS-DEPLOY.md](NAS-DEPLOY.md).

## Hallow family

Named pipe `HallowFamily.Assist.<user>` (`ping`, `status`, `open`, `menu`, `invoke`, `quit`, `disconnect` - there's no
command that starts a session), `HallowAssist.exe --cmd "<line>"`, `%APPDATA%\HallowAssist\status.json` (never a code or
password), the one Hallow icon (Hallow Hub hosts the tray menu), the Hallow accent from Hallow Toolkit, crash reports kept on
this PC, and updates from GitHub releases (asks first, checks the SHA-256).

## Building

- App: `.\build.ps1` (the C# compiler that ships with Windows, .NET Framework 4.8, WPF built in code). `.\build.ps1 -Tests`
  also builds `tests\AssistTests.exe`.
- Relay: `cd server; npm test` (Node 20+, no packages). Integration tests (relay + two app copies in test mode + headless
  Edge): `node tests\integration.mjs <work folder>`.
- `HallowAssist.exe --snap <folder>` renders every window to PNGs with made-up data.
- Icons: Font Awesome Free 7.3.1 (CC BY 4.0), generated with `tools\gen-icons.ps1`.

## Not yet

See [ROADMAP.md](ROADMAP.md): helping Android phones and TVs, iPhone viewing, an admin service for UAC prompts, H.264,
sound in the browser viewer, several helpers at once, remembering the people you help (Your PCs covers your own PCs).

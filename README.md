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
  is the strongest check against a relay that tries to sit in the middle. Your relay operator (you) can see *that* two
  people are connected, from which addresses, and for how long.

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
sound, several helpers at once, an address book.

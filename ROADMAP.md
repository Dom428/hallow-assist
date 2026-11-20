# Hallow Assist roadmap

## Done

| Version | What |
|---|---|
| 1.0 | Get help (9-digit code + one-time password, Allow/Deny with a safety code, always-on-top bar, Stop control, Ctrl+Alt+End and close-to-disconnect), Help someone in the app and in any browser (monitors, mouse/keyboard, touch gestures, on-screen keyboard, chat, clipboard text, files both ways with a prompt, quality, stats), end-to-end encryption (ECDH P-256 + PBKDF2/HKDF bound to the password, key confirmation, AES-256-GCM with counters), DXGI Desktop Duplication with dirty rectangles and a GDI fallback, JPEG tiles with adaptive quality, UAC / admin-window detection, unattended access for your own PCs (opt-in), your own relay (Node, no packages, Docker, Cloudflare Tunnel guide), Hallow family link |

## Later

| Idea | Why it isn't in 1.0 |
|---|---|
| Help **Android phones and TVs** | Needs a native Android app: MediaProjection for the picture and an AccessibilityService for taps and keys. Building it needs the Android SDK, which isn't on this PC - it needs your OK to download it. The relay and the protocol are ready for it. |
| **iPhone / iPad** | Apple only allows screen *viewing* through a ReplayKit broadcast extension, never control. Viewing could come with an iOS app, which needs a Mac with Xcode to build. Helping *from* an iPhone already works in Safari. |
| **Samsung / LG / Roku TVs** (being helped) | Their systems give apps no way to capture the screen or send input. Helping *from* a TV browser already works. |
| **Admin service** for UAC prompts, the lock screen and admin windows | Needs a Windows service running as SYSTEM on the secure desktop - installing it needs admin rights and careful review. 1.0 detects these and tells the helper "They need to click the admin prompt themselves". |
| **H.264** video | Much less data for moving pictures (videos, scrolling). JPEG tiles are simple and every browser draws them; H.264 needs Media Foundation encoding and WebCodecs/MSE in the browser. |
| **Sound** | WASAPI loopback capture + Opus, and a way to play it in the browser. |
| **Several helpers** in one session | v1 allows exactly one helper per code - simpler and safer. |
| **Address book** | Remember your own unattended PCs and the people you help. |
| **PAKE** (CPace / SPAKE2) | Even stronger password check against a dishonest relay than the current password-bound key agreement + safety code. |
| Default relay built in | Once your relay is live, a version with its address preset so friends don't have to type it. |

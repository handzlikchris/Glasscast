# Glasscast

https://github.com/user-attachments/assets/8f886138-fdfd-4640-8a2e-975cf590e1ee

**Phone remote control for Meta Ray-Ban Display glasses.** Every app on your Android phone, on
the glasses' display, today: pinch the air to tap, swipe your thumb to scroll, say what to type.
No waiting for glasses versions of your apps.

*Tech preview* · Android 11+ companion app · Meta Ray-Ban Display + Neural Band · also a remote
desktop for Windows PCs

## What it does

The glasses have only a handful of apps of their own. Your phone already has every app you use:
WhatsApp, Gmail, Maps, Spotify, ChatGPT, Claude, the one you wrote yourself. Glasscast puts them on
the glasses as they are, with nothing for their makers to build.

They look right there, too. The glasses' display is a 600×600 square, and **Square screen** in
the companion turns the phone's screen into a 1080×1080 square, so apps lay themselves out
for it: the whole app fills the display, no letterboxing, text big enough to read. **Reset
screen** puts the phone back when you're done. (Square screen needs a one-off permission granted over
ADB; without it the view still follows the app in front, letterboxed.)

Your phone sits on the table, on a stand or in your bag. The glasses show its screen live, and
the Neural Band works it like a laptop touchpad:

- **See the phone live.** Its screen streams to the glasses over WebRTC (the phone's hardware
  H.264 encoder), and the view follows the app in front, pop-up windows and split screen
  included.
- **Tap, scroll, drag.** Pinch to tap, two quick pinches to double tap, swipe your thumb to
  scroll. Pinch, then pinch and hold, to put a finger down: drag, select text, long press.
- **Get around.** Back, Home, notifications and the app overview, all from the glasses.
- **Type by voice or handwriting.** The glasses' own composer fills a text box; Glasscast types
  it into the phone and presses the app's **Send** button (WhatsApp, ChatGPT and Claude all treat
  Enter as a new line, so it finds the real button).
- **Control a Windows PC too.** The same app, pointed at your PC: a region of the screen with
  its sound, the mouse and keyboard, and one-swipe jumps between your apps.

## Controls

The same habits on both targets. **?** on the glasses' bar shows them during a session.

| Neural Band | Phone | PC |
| --- | --- | --- |
| Pinch and move | Move the cursor | Move the cursor |
| Pinch | Tap | Click |
| Two quick pinches | Double tap | Double-click |
| Pinch, then pinch and move | Drag a finger (select, move, draw) | Drag with the button held |
| Pinch, then pinch and hold | Long press | Held click |
| Swipe up / down | Scroll | Scroll (or push the cursor past the edge) |
| Swipe left / right | Page sideways | Pan the view (with **Pan** on) |
| Swipe left twice | App overview: swipe through, pinch to pick | Next app shortcut |
| Swipe right twice | Type | Type |
| Swipe down twice | Back | |
| Middle-finger pinch | The control bar | The control bar |

## How it works

```
                 first connection only: pairing, proof, WebRTC setup (opaque to the server)
glasses web app ───────────────────────────► server (your Windows PC) ◄─────────── companion app
       ▲   │                                                                        │     ▲
       │   └── WebRTC DataChannel: taps, swipes, Back/Home, text ─────────────────►│     │
       └────── WebRTC video: the phone's screen (MediaProjection, hardware H.264) ──┘  accessibility
                       (through Meta's app on the phone, its link to the glasses)        service
```

- The **glasses** run a web app (React, 600×600). The **phone** runs the companion: it captures
  the screen with Android's MediaProjection and turns the glasses' input into taps and text
  through an accessibility service. No root, no ADB at run time.
- A web page on the glasses can't reach a phone directly (it needs HTTPS and the phone has no
  public name or certificate), so the two **meet once through a server** both can reach: your
  PC, behind Caddy. It relays the setup messages unread and then steps aside. The video and your
  input go phone ⇄ glasses, never through the server, and a session carries on if the PC goes
  away.
- **The phone is the gate.** New glasses pair with the phone itself: the same six-digit code on
  both screens (ECDH with a commitment, as Bluetooth does), approved on the phone. Every session
  proves that pairing, signs the WebRTC setup with the session key, and needs Android's
  screen-capture consent tapped on the phone. The server holds no key and can't approve
  anything.

Design notes for every part live in [`architecture/`](architecture/README.md); phone mode in
[`architecture/phone-mode.md`](architecture/phone-mode.md).

## Good to know (tech preview)

- **Tested on one set:** a Samsung Galaxy S25 (Android 16) with Meta Ray-Ban Display. Other
  Android 11+ phones should work, untested.
- **The phone stays awake and unlocked** during a session: Android stops screen capture when
  the phone locks. The companion keeps the screen on while you're connected.
- **The phone needs internet.** The glasses reach the phone through Meta's app, which drops
  that link when the phone goes offline.
- **Apps that block screenshots** (banking, DRM video) show black.
- **No iPhone.** iOS doesn't let an app tap or type into other apps.
- **You host the meeting point:** a Windows PC reachable over HTTPS (a domain name and two port
  forwards). One phone per server. There's no hosted version and no APK release yet: you build
  the companion yourself.

## Getting started

### 1. The server (Windows PC)

You need Windows 10/11, the [.NET 10 SDK](https://dotnet.microsoft.com/download), Node 20+,
[Caddy](https://caddyserver.com) (stock build), a domain name you can add a DNS record to, and a
router that forwards ports from a public IP (not behind CGNAT).

1. **Router:** give the PC a fixed LAN address, then forward **TCP 443 → the PC, port 8443**
   (Caddy) and **UDP 50000 → the PC, port 50000** (PC mode's video). Leave everything else
   closed, RDP above all.
2. **DNS:** an A record for your host name (e.g. `glasses.example.com`) pointing at your public
   IP. Optionally a CAA record allowing only `letsencrypt.org`.
3. **Firewall:** run `deploy\firewall.ps1` in an admin PowerShell (TCP 8443, UDP 50000).
4. **Config:** copy `server/appsettings.Local.example.json` to `server/appsettings.Local.json`
   and set `Web:PublicHost` (your host name) and `Media:PublicIp` (your public IP). With two
   network adapters, also set `Media:BindAddress` to the one the router forwards to.
5. **Run** the server (it lives in the tray) and Caddy, which gets its certificate by itself:

   ```powershell
   .\scripts\run.ps1
   $env:GLASSES_HOST = 'glasses.example.com'; caddy run --config deploy\Caddyfile
   ```

6. Check `https://glasses.example.com/health` from your phone's mobile data: a padlock and `ok`.

### 2. The phone (companion app)

Build it with JDK 17 and the Android SDK, or open `android-companion/` in Android Studio. The
Gradle wrapper isn't committed yet: create it once as the [companion README](android-companion/README.md#build)
shows. Then build and install:

```powershell
cd android-companion
.\gradlew.bat assembleDebug
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

On the phone, open **Glasscast**, enter `wss://glasses.example.com/ws/companion` and tap
**Pair**; approve the popup on the PC. Turn the app on under Settings > Accessibility (a
sideloaded app first needs **Allow restricted settings** in its App info menu), then tap
**Start**. Details, including the optional square screen for the glasses: the
[companion README](android-companion/README.md).

### 3. The glasses

Open `https://glasses.example.com` as a web app on the glasses (see Meta's
[web app docs](https://wearables.developer.meta.com/docs/develop/webapps)) and choose **Phone**.
The first time, the glasses and the phone show the same code: tap **Approve** on the phone. Then
tap **Start** on Android's screen-capture prompt, and the phone's screen appears.

For **PC mode**, choose **PC** instead and click **Approve** in the popup on the PC. For 24 hours
after that, the same glasses reconnect without asking. End a PC session from the tray or with
**Ctrl+Alt+Shift+X**.

## PC mode

Everything above, for a Windows desktop: the glasses view a region of the primary monitor
(an orange frame shows it on the PC) over low-latency WebRTC video, with the PC's sound, and drive
the mouse and keyboard. App shortcuts (`Apps:Shortcuts` in `appsettings.Local.json`) bring an open
app to the front and fit it to the region, so **swipe left twice** cycles through them. A
single-use pairing approved on the PC gates every session; the approval also remembers the
glasses for 24 hours with a device token that changes on every use.

Only TCP 443 (to Caddy) and UDP 50000 are public; the server itself listens on loopback. The
protocol is a strict allowlist, typed text never presses Enter by itself, and anything odd
(rejected pairings, bad tokens, wrong origins) raises a notification on the PC. More in
[`architecture/pairing-and-auth.md`](architecture/pairing-and-auth.md).

## Development

| Folder | What it is |
| --- | --- |
| `server/` | ASP.NET Core + WinForms app, runs as the logged-in user: pairing, WebRTC, capture, input, tray, the phone relay |
| `client-web/` | The glasses app: Vite + React + TypeScript (600×600) |
| `android-companion/` | The phone companion (Kotlin): screen capture, WebRTC, accessibility input |
| `tests/` | Server tests (xUnit): unit, WebSocket integration, real H.264 encoder |
| `tools/e2e-harness/` | Dev-only host + headless-Chrome script that drives the whole PC flow |
| `deploy/` | Caddyfile and the Windows firewall script |
| `scripts/` | `run.ps1`: build the client and start the server |

Try the glasses app in a desktop browser (PC mode; 600×600 is the glasses' size):

```powershell
.\scripts\run.ps1 -Dev      # then open http://127.0.0.1:5080 and approve the popup
.\scripts\run.ps1 -Lan      # from another device on your network (firewall.ps1 -LanTesting first)
```

Phone mode needs HTTPS (the pairing uses Web Crypto), so test it through Caddy.

Tests:

```powershell
dotnet test                                                  # server
cd client-web; npm test; npx tsc --noEmit                    # glasses app
cd android-companion; .\gradlew.bat testDebugUnitTest        # companion
dotnet run --project tools/e2e-harness                       # then, in tools/e2e-harness/browser:
npm run drive                                                # full PC flow in headless Chrome
```

## Status

| Part | State |
| --- | --- |
| Phone mode | Used from the glasses: video, taps, scrolling, drags, app overview, typing with Send, pairing on the phone, sessions that outlive the server |
| PC mode | Works end to end on the glasses: video with sound, pointer, scrolling, typing, app shortcuts |
| Not yet | Rotation, the phone's sound, an APK release, a hosted meeting point, TURN for networks that block UDP |

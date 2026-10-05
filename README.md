# Glasscast

**Phone remote control for Meta Ray-Ban Display glasses.** Every app on your Android phone, on
the glasses' display, today: pinch the air to tap, swipe your thumb to scroll, say what to type.
No waiting for glasses versions of your apps.

*Tech preview* · Android 11+ companion app · Meta Ray-Ban Display + Neural Band · also a remote
desktop for Windows PCs

**Try it in a few minutes:** [install the app on your phone, scan a QR code, type a
code](#getting-started).

https://github.com/user-attachments/assets/8f886138-fdfd-4640-8a2e-975cf590e1ee

## What it does

The glasses have only a handful of apps of their own. Your phone already has every app you use:
WhatsApp, Gmail, Maps, Spotify, ChatGPT, Claude, the one you wrote yourself. Glasscast puts them on
the glasses as they are, with nothing for their makers to build.

They look right there, too. The glasses' display is a 600×600 square, and **Square screen** in
the companion turns the phone's screen into a 1080×1080 square, so apps lay themselves out
for it: the whole app fills the display, no letterboxing, text big enough to read. **Reset
screen** puts the phone back when you're done. (Square screen needs a [one-off permission](#square-screen-recommended-one-off-setup) granted over
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

## Getting started

The quick way uses the relay I host at `glasscast.reliable-solutions.co.uk`: install one app on
the phone, scan one QR code, type one code. Nothing to set up on a computer. (Prefer your own
server? See [Host it yourself](#host-it-yourself). What the relay can and can't see:
[About the hosted relay](#about-the-hosted-relay).)

### 1. The phone: install the companion app

1. On the phone, download
   **[glasscast-companion.apk](https://github.com/handzlikchris/Glasscast/releases/latest/download/glasscast-companion.apk)**
   (from the [Releases](https://github.com/handzlikchris/Glasscast/releases) page) and open it.
   Android asks to allow installing apps from your browser (or Files): allow it, then
   **Install**.
2. Open **Glasscast** and allow notifications when asked.
3. **Connect to the server:** the address is already filled in
   (`wss://glasscast.reliable-solutions.co.uk/ws/companion`). Tap **Pair**: the status card
   shows **Server: Paired**.
4. **Allow input:** tap **Open accessibility settings** → Installed apps → **Glasscast** → on.
   If it's greyed out ("Restricted setting"): Settings → Apps → **Glasscast** → ⋮ (top right) →
   **Allow restricted settings**, then turn it on. This is how the glasses tap and type on the
   phone; Glasscast only acts on what the glasses send during a session.
5. Tap **Start**. A notification stays while the companion is ready for the glasses.

Then two optional steps that make the phone much easier to use from the glasses: the
[square screen](#square-screen-recommended-one-off-setup) and
[a small keyboard](#a-small-keyboard-optional-recommended).

#### Square screen (recommended, one-off setup)

The glasses' display is square, the phone's isn't, so by default the glasses show the phone's
screen with black bars. **Square screen** in the companion makes the phone's screen square
(1080×1080) while you use the glasses: apps then lay themselves out to fill the display, and
text comes out much bigger. **Reset screen** puts the phone back. Both are one tap in the app,
any time.

Changing the screen size needs a permission Android only gives through ADB, so it takes a
**one-off step with a computer**. You do it once: the phone keeps the permission through
restarts and app updates (only uninstalling the app drops it).

1. On the phone: Settings → About phone (on Samsung, then Software information) → tap **Build
   number** seven times (this turns on Developer options), then Settings → Developer options →
   **USB debugging** on.
2. On a computer, get Android's
   [platform-tools](https://developer.android.com/tools/releases/platform-tools) (it contains
   `adb`), connect the phone by USB and allow the computer when the phone asks.
3. Run:

   ```
   adb shell pm grant uk.co.reliablesolutions.glassesremote.companion android.permission.WRITE_SECURE_SETTINGS
   ```

That's it: unplug, and use **Square screen** / **Reset screen** in the app whenever you like
(change it before starting a session). The ⓘ next to them in the app shows the same steps and
copies the command for you. USB debugging can be turned off again afterwards.

#### A small keyboard (optional, recommended)

When an app on the phone opens a text field, its keyboard pops up on the phone's screen, and
that's the screen the glasses show. The stock **Samsung Keyboard** takes about half of it, so
once you're in a text field you hardly see the app any more; on the square screen it covers
nearly everything (it ignores the square size). You type from the glasses anyway (voice or
handwriting), so the phone's keyboard only has to stay out of the way:

1. Install [Gboard](https://play.google.com/store/apps/details?id=com.google.android.inputmethod.latin)
   and make it the default keyboard (Settings → General management → Keyboard list and default).
2. Shrink it: in Gboard's toolbar, **Resize** and drag its top edge down (or **Floating**).
3. In Gboard's settings, turn off the number row and the suggestion strip.

Swipe down twice on the glasses to put the keyboard away. A keyboard that switches to a small
one by itself on the square screen is planned.

### 2. The glasses: add Glasscast

Scan this with the **phone's camera** (not the glasses). It opens the Meta AI app, which asks
to add Glasscast to your glasses:

<img src="docs/images/add-to-glasses-qr.png" alt="QR code: add Glasscast to Meta Ray-Ban Display" width="50%">

Or add it by hand: Meta AI app → **Devices** → **Display Glasses settings** → **App
connections** → **Web apps** → **Add a web app**, name `Glasscast`, URL
`https://glasscast.reliable-solutions.co.uk/`. If the Meta AI app doesn't offer web apps, turn
on developer mode for the glasses (see Meta's
[web app docs](https://wearables.developer.meta.com/docs/develop/webapps)).

### 3. Connect them (once)

1. On the glasses open **Glasscast** and pinch **Phone**. The glasses show a **connect code**
   (like `ABC 234`).
2. In the Glasscast app on the phone, under **Connect glasses**, type it and tap **Connect**.
3. Both screens now show the same **six-digit pairing code**. Check they match and tap
   **Approve** in the app (or in its notification).
4. Tap **Start** on Android's screen-sharing prompt. The phone's screen appears on the glasses.

From then on: open Glasscast on the glasses, pinch **Phone**, and tap **Start** on the phone.
The glasses remember which phone is theirs and that they're paired.

**Keep the phone's Wi-Fi switched on**, even when it isn't connected to any network. The glasses
then talk to the phone over a direct Wi-Fi link, which is fast; with the phone's Wi-Fi off they
fall back to Bluetooth, and the video gets laggy.

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

### Getting around

**The glasses' Back gesture (middle-finger pinch) doesn't go back on the phone.** It opens
Glasscast's **menu bar** at the top of the view; Back again closes it and you're back on the
phone's screen. On the bar, swipe left / right to move between buttons and pinch to press one:

| Button | Does |
| --- | --- |
| **Back** | The phone's Back (the same as **swiping down twice**, which needs no bar) |
| **Home** | The phone's home screen |
| **Apps** | The app overview: swipe left / right through your apps, pinch to pick, Back to leave (or **swipe left twice** from anywhere) |
| **Notif** | The notification shade |
| **Type** | A text box for the glasses' voice or handwriting composer: **Send text**, then **Send** (or **swipe right twice**) |
| **↕** | How far one scroll swipe goes, remembered per app |
| **?** | The gestures and shortcuts, on the glasses |
| **End** | Ends the session and goes back to Glasscast's start screen |

While the bar is closed, every pinch and swipe goes to the phone. Two quick
middle-finger pinches belong to the glasses (they turn the display off and on).

## How it works

```
                 first connection only: pairing, proof, WebRTC setup (opaque to the server)
glasses web app ──────────────────► server (the hosted relay, or your own) ◄────── companion app
       ▲   │                                                                        │     ▲
       │   └── WebRTC DataChannel: taps, swipes, Back/Home, text ─────────────────►│     │
       └────── WebRTC video: the phone's screen (MediaProjection, hardware H.264) ──┘  accessibility
                       (through Meta's app on the phone, its link to the glasses)        service
```

- The **glasses** run a web app (React, 600×600). The **phone** runs the companion: it captures
  the screen with Android's MediaProjection and turns the glasses' input into taps and text
  through an accessibility service. No root, no ADB at run time.
- A web page on the glasses can't reach a phone directly (it needs HTTPS and the phone has no
  public name or certificate), so the two **meet once through a server** both can reach: the
  hosted relay, or your own. It relays the setup messages unread and then steps aside. The video
  and your input go phone ⇄ glasses, never through the server, and a session carries on if the
  server goes away.
- **The phone is the gate.** New glasses pair with the phone itself: the same six-digit code on
  both screens (ECDH with a commitment, as Bluetooth does), approved on the phone. Every session
  proves that pairing, signs the WebRTC setup with the session key, and needs Android's
  screen-capture consent tapped on the phone. The server holds no key and can't approve
  anything.

Design notes for every part live in [`architecture/`](architecture/README.md); phone mode in
[`architecture/phone-mode.md`](architecture/phone-mode.md).

## About the hosted relay

The relay is a convenience: it's the meeting point the glasses and the phone use to find each
other, because a page on the glasses can't reach a phone directly (see
[How it works](#how-it-works)). What it does and doesn't see:

- It passes the **first connection's setup** between the glasses and your phone: the pairing
  exchange (public keys and commitments), each session's proof, and the signed WebRTC setup. It
  holds no key and can't approve anything: the phone decides, and a relay that tampered would
  make the two pairing codes differ.
- **The screen video and your input never go through it.** They go straight between the phone
  and the glasses over WebRTC, encrypted end to end (DTLS-SRTP), and a session carries on if the
  relay goes away.
- It keeps, per phone, a random id, a hash of the companion's token and the phone's model name,
  and its web server logs connections (IP addresses), as any website does.
- It also **serves the glasses app**: the page your glasses run comes from this server. You're
  trusting it to serve the code in this repository, as with any web app. That's the one thing
  self-hosting removes.

So for the most privacy, run your own: the relay alone is a small server that holds nothing
of yours and can't control the machine it runs on. See [Host it yourself](#host-it-yourself).

## Good to know (tech preview)

- **Tested on one set:** a Samsung Galaxy S25 (Android 16) with Meta Ray-Ban Display. Other
  Android 11+ phones should work, untested.
- **The phone stays awake and unlocked** during a session: Android stops screen capture when
  the phone locks. The companion keeps the screen on while you're connected.
- **The phone needs internet.** The glasses reach the phone through Meta's app, which drops
  that link when the phone goes offline.
- **The phone's Wi-Fi must be on** (connected to a network or not): Meta's app links the glasses
  to the phone over Wi-Fi Direct when it can. With Wi-Fi off it uses Bluetooth, which is too slow
  for smooth video.
- **Apps that block screenshots** (banking, DRM video) show black.
- **No iPhone.** iOS doesn't let an app tap or type into other apps.
- **The APK is sideloaded** (not on the Play Store), hence Android's "restricted setting" step
  for accessibility.

## Host it yourself

Two ways, depending on what you want:

- **Just the phone, on your own relay:** `relay-server/` is a small server with the glasses app
  and the relay and nothing else (no screen capture or input code, so it can't control the box
  it runs on). Windows (IIS or Caddy) or Linux. `.\scripts\publish-relay.ps1` builds it;
  [`architecture/deployment-and-networking.md`](architecture/deployment-and-networking.md#hosted-phone-relay-relay-server-2026-10-05)
  has the steps. Then build the companion pointed at it
  (`.\gradlew.bat assembleDebug -PglassesServer=wss://your-host/ws/companion`) or type the
  address into the app, and make your own add-to-glasses QR code: it encodes
  `fb-viewapp://web_app_deep_link?appName=Glasscast&appUrl=<your https URL, URL-encoded>`.
- **Phone and PC, on your Windows PC:** the full server below (PC mode needs it anyway). It
  also relays for the phone; there the companion registers through an Approve popup on the PC.

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

Use the [released APK](https://github.com/handzlikchris/Glasscast/releases/latest/download/glasscast-companion.apk)
and type your server's address into it, or build it yourself with JDK 17 and the Android SDK
(or open `android-companion/` in Android Studio). The Gradle wrapper isn't committed yet: create
it once as the [companion README](android-companion/README.md#build) shows. Then build and
install:

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

Add `https://glasses.example.com` as a web app on the glasses (by hand in the Meta AI app, or
with your own QR code as above) and choose **Phone**. With one phone approved on the PC, the
glasses go straight to it; with more they show a connect code, as on the hosted relay. Then the
pairing code (Approve on the phone) and **Start** on Android's screen-capture prompt.

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
| `relay/` | The phone relay as a library (no Windows, capture or input code), shared by both servers |
| `relay-server/` | The headless relay: glasses app + relay, Windows or Linux |
| `deploy/` | Caddyfiles, the Windows firewall script, the relay's start scripts and systemd unit |
| `scripts/` | `run.ps1`: build the client and start the server; `publish-relay.ps1`: package the relay |

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
| Hosted relay | Running at `glasscast.reliable-solutions.co.uk`, with a released companion APK (tech preview) |
| Not yet | Rotation, the phone's sound, TURN for networks that block UDP, the Play Store |

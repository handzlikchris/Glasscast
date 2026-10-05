# Glasscast companion (Android)

The phone side of **phone mode** (design: `architecture/phone-mode.md`). It keeps a connection
to the PC so the glasses can reach it to start a session. The phone decides who gets in: new
glasses pair here (the same code on both screens, Approve in a notification), paired glasses
prove themselves each session, and Android's screen-capture consent is tapped here every time.
Then it streams the screen to the glasses over WebRTC and turns their input into taps, swipes,
Back/Home/Recents and text through an accessibility service. The PC only relays the setup; once
the glasses are connected the session doesn't need the PC (the phone must stay online: Meta's app
drops its link to the glasses without internet).

Status: used from the glasses (2026-09-28, over the LAN). Pairing on the phone and sessions that
outlive the PC built on 2026-09-29 (21 unit tests pass); not yet tried on the device.

## Build

Needs JDK 17 (Gradle 9 won't run on the JDK 11 in `JAVA_HOME` on this PC) and the Android SDK
(platform 35; AGP 9.0 installs build-tools 36 itself). The wrapper jar and scripts are not
committed; create them once.

```powershell
cd android-companion
# once: any installed Gradle, in an empty folder (Gradle 7.6 can't load this project), then copy
# gradlew, gradlew.bat and gradle/wrapper/gradle-wrapper.jar here
gradle wrapper --gradle-version 9.1.0
$env:JAVA_HOME = 'C:\Program Files\Eclipse Adoptium\jdk-17.0.13.11-hotspot'
.\gradlew.bat assembleDebug testDebugUnitTest
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

Or open `android-companion/` in Android Studio.

## Set up on the phone (once)

1. Open **Glasscast** and enter the server's companion address,
   `wss://<your host>/ws/companion` (a build can fill it in: `glassesServer=wss://...` in
   `android-companion/local.properties`, git-ignored). Tap **Pair**. On your PC's server, the PC
   shows a "Phone pairing request" popup with the same code: approve it there. A hosted relay
   (`relay-server/`) registers the phone at once. (This only lets the phone use the server as a
   meeting point; the glasses pair with the phone itself, below.)
2. **Accessibility:** Settings > Accessibility > Installed apps > Glasscast > on. A
   sideloaded app is greyed out at first ("Restricted setting"): Settings > Apps >
   Glasscast > ⋮ > **Allow restricted settings**, then turn it on.
3. Tap **Start** (the same button then says **Stop**). A notification stays while the companion is
   connected (Stop is there too).
4. Optional, **square screen for the glasses:** grant a one-off permission from a PC (it survives
   restarts; ⓘ on the setup screen shows this too):
   `adb shell pm grant uk.co.reliablesolutions.glassesremote.companion android.permission.WRITE_SECURE_SETTINGS`.
   Then **Square screen** makes the phone 1080×1080 (its short side) at density 320, and **Reset
   screen** puts it back, any time. Change it before starting a session: the companion reads the
   screen size when a session starts.
5. **Keyboard:** use Gboard, made small. Samsung Keyboard ignores the square screen and covers most
   of it. In Gboard: toolbar → **Resize** (drag the top edge down) or **Floating**, and in its
   settings the number row and suggestion strip off. Typing comes from the glasses, so the keyboard
   only needs to stay out of the way. (Switching keyboards with the square screen, or an invisible
   keyboard in the companion: `.claude/tasks/square-screen-keyboard.md`.)

## A session

On the glasses choose **Phone**. The first time on a server (or after Pair again) the glasses may
show a **connect code** instead (always on a hosted relay, or when the PC knows several phones):
type it under **Connect glasses** in the companion while it runs. From then on the glasses ask
for this phone by its id. The first time (or after Pair again / Forget the glasses), the
glasses show a code and the phone a notification "Pair glasses? Code …": if the codes match, tap
**Approve**. Then the phone shows the screen-capture prompt (or a notification to open it): tap
**Start**. The glasses then see the whole screen; the notification offers
**End session**, and Android's status-bar chip can stop it too. Locking the phone ends it
(Android 15+); the companion keeps the screen on while a session is live. Losing the PC doesn't;
losing the phone's internet does (Meta's app drops the glasses' link), as do 15 s without the
glasses' ping.

## Files

| File | Role |
| --- | --- |
| `CompanionService.kt` | Foreground service: the server connection, relays, connect codes (`claim`), the pairing prompt, the session's life |
| `ConnectCode.kt` | A typed connect code in the server's shape ("abc 234" → "ABC-234"; unit-tested) |
| `GlassesRelay.kt` | The phone's side of a glasses relay: pairing (code, Approve), proof, the session key |
| `GlassesTrust.kt` | The crypto: ECDH P-256, HMACs (same test vector as the glasses' `phoneTrust.ts`) |
| `ServerLink.kt` | `Prefs` (server, token, paired glasses), `ServerLink` (auth, ping, reconnect), `Pairing` |
| `ConsentActivity.kt` | Android's screen-capture consent (whole display) |
| `ScreenSession.kt` | libwebrtc: screen capture → H.264 track, DataChannel `input`, coordinate mapping; signed offer, checked answer, ends when the glasses go quiet |
| `CropProcessor.kt` | Crops frames to the region, long side ≤ 600 |
| `InputService.kt` | Accessibility service: gestures, global actions, text into the focused field, keep screen on |
| `InputProtocol.kt` | Strict parser for the glasses' DataChannel messages (unit-tested) |
| `MainActivity.kt` | Setup screen: a status card (companion, server, glasses, input, screen) and one card per step, Material-style with plain views and the device theme's colours; Start/Stop in one button (the first card once paired; before that, Pair leads, and once paired Unpair replaces it); Connect glasses (the connect code); Square screen / Reset screen and the ⓘ setup pop-up |
| `DisplayOverride.kt` | Square screen and reset: the window manager's forced size and density (hidden API via HiddenApiBypass; needs `WRITE_SECURE_SETTINGS`, granted once over adb) |

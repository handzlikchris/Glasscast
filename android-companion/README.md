# Glasses Remote companion (Android)

The phone side of **phone mode** (design: `architecture/phone-mode.md`). It keeps a connection
to the PC, and when the glasses start a phone session it asks for Android's screen-capture
consent, streams the screen to the glasses over WebRTC and turns their input into taps, swipes,
Back/Home/Recents and text through an accessibility service. The PC only relays signalling.

Status: written 2026-09-27, **not built or run yet**.

## Build

Needs JDK 17 and the Android SDK (platform 35). Gradle 9.1 and AGP 9.0 (the wrapper downloads
Gradle on first use; the wrapper jar and scripts are not committed).

```powershell
cd android-companion
gradle wrapper --gradle-version 9.1.0     # once, creates gradlew (any installed Gradle will do)
.\gradlew.bat assembleDebug testDebugUnitTest
adb install -r app\build\outputs\apk\debug\app-debug.apk
```

Or open `android-companion/` in Android Studio.

## Set up on the phone (once)

1. Open **Glasses Remote**. The address defaults to
   `wss://glasses.example.com/ws/companion`. Tap **Pair**: the PC shows a
   "Phone pairing request" popup with the same code. Approve it there.
2. **Accessibility:** Settings > Accessibility > Installed apps > Glasses Remote > on. A
   sideloaded app is greyed out at first ("Restricted setting"): Settings > Apps > Glasses
   Remote > ⋮ > **Allow restricted settings**, then turn it on.
3. Tap **Start**. A notification stays while the companion is connected (Stop is there too).

## A session

On the glasses choose **Phone**. The phone shows the screen-capture prompt (or a notification
to open it): tap **Start**. The glasses then see the whole screen; the notification offers
**End session**, and Android's status-bar chip can stop it too. Locking the phone ends it
(Android 15+); the companion keeps the screen on while a session is live.

## Files

| File | Role |
| --- | --- |
| `CompanionService.kt` | Foreground service: the PC connection, the session's life (asking → live → ended) |
| `ServerLink.kt` | `Prefs` (server, token), `ServerLink` (auth, ping, reconnect), `Pairing` |
| `ConsentActivity.kt` | Android's screen-capture consent (whole display) |
| `ScreenSession.kt` | libwebrtc: screen capture → H.264 track, DataChannel `input`, coordinate mapping |
| `CropProcessor.kt` | Crops frames to the region, long side ≤ 600 |
| `InputService.kt` | Accessibility service: gestures, global actions, text into the focused field, keep screen on |
| `InputProtocol.kt` | Strict parser for the glasses' DataChannel messages (unit-tested) |
| `MainActivity.kt` | Setup screen |

# Architecture docs

One file per feature area. Each says what the feature does, which files own it, how the
pieces talk, the rules it must keep, and where its tests are. Read the one for the area
you are about to change; `CLAUDE.md` stays the entry point (stack, commands, invariants,
status) and these go one level deeper.

| Doc | Read it when you touch |
| --- | --- |
| [pairing-and-auth.md](pairing-and-auth.md) | `/ws/pair`, approval tokens, device tokens (resume), `PairingCoordinator`, alerts, rate limits |
| [session-and-protocol.md](session-and-protocol.md) | `/ws/session`, `ControlSession`, message types (`ControlProtocol.cs` ⇄ `protocol.ts`), timeouts, close reasons |
| [media-pipeline.md](media-pipeline.md) | capture, H.264/VP8 encoding, `FramePump`, RTP packetizing, pacing, NACK/PLI, bitrate, SDP, SRTP |
| [audio.md](audio.md) | the PC's sound: loopback capture, Opus, the audio track, DTX, the ♪ toggle, V/A bandwidth |
| [stats-and-diagnostics.md](stats-and-diagnostics.md) | the Stats panel, `mediaStats`/`stats` messages, the JSONL stats log, the link test |
| [input-and-desktop.md](input-and-desktop.md) | modes, regions and geometry, `SendInput`, app shortcuts / window switching, cast area, keep-awake |
| [glasses-client.md](glasses-client.md) | the React app: screens, gestures, focus navigation, Back, Type flow, local storage |
| [pc-ui.md](pc-ui.md) | tray icon and menu, approve popup, session banner, cast frame, hotkey, alert notifications |
| [deployment-and-networking.md](deployment-and-networking.md) | Caddy, router/ports, IIS, adapters, firewall, launch profiles, configuration keys |
| [phone-mode.md](phone-mode.md) | controlling the Android phone: why a server only for the first connection, pairing on the phone, `PhoneRelay`, the DataChannel protocol, sessions that outlive the internet (plan + status) |
| [testing.md](testing.md) | xUnit layout, `TestServerHost` and fakes, the e2e harness and its hooks, isolated builds |

## System in one picture

```
Glasses WebView (600x600)            router            This PC (one process, logged-in user)

client-web (React)       --HTTPS/WSS :443-->  Caddy :8443 (TLS, path allowlist)
  PairingScreen                                 --> Kestrel 127.0.0.1:5080
  SessionScreen                                       /ws/pair    -> PairingCoordinator
    connection.ts (tokens)                            /ws/session -> ControlSession
    rtc.ts (receive-only video + audio)                  InputController -> SendInput
         ^                                               FramePump -> GDI capture -> H.264
         |                                                 -> H264Rtp -> RtpPacer -> SIPSorcery
         |                                               AudioPump -> process loopback -> Opus -> SIPSorcery
         +------------- UDP 50000 (SRTP, port forward) <------------------------+
                                                     WinForms UI thread: TrayApp, ApprovePopup,
                                                     SessionBanner, CastFrame
```

## Life of a session (cross-reference)

1. Page loads. A remembered device token in localStorage → straight to a session (`resume`);
   otherwise `/ws/pair` → code on both screens → **Approve** on the PC → approval token down
   the pair socket. ([pairing-and-auth.md](pairing-and-auth.md))
2. `/ws/session`: first message `authenticate`/`resume` within 3 s → `authenticated`
   (carries a fresh device token) → `hello` → `rtcOffer`. ([session-and-protocol.md](session-and-protocol.md))
3. The browser answers; ICE checks go to the router's public IP:50000; SIPSorcery learns the
   glasses as peer-reflexive; DTLS-SRTP; `FramePump` starts. ([media-pipeline.md](media-pipeline.md))
   The glasses send their ♪ setting; with it on, `AudioPump` sends the PC's sound. ([audio.md](audio.md))
4. Gestures become `move`/`click`/`scroll`/`typeText`/`key`/`switchApp`/`setRegion`/`setMode`,
   gated by mode on the server. ([input-and-desktop.md](input-and-desktop.md), [glasses-client.md](glasses-client.md))
5. Once a second: PC → `mediaStats`, glasses → `stats`, both into the stats log.
   ([stats-and-diagnostics.md](stats-and-diagnostics.md))
6. Ends on: glasses close/hidden 5 s, heartbeat silence 15 s, idle 60 min, 4 h cap, tray /
   hotkey terminate, protocol violation, media failure, or the same device resuming elsewhere.

## Keeping these docs true

- Change a feature → update its doc in the same commit (or the next `docs:` commit).
- Facts that everyone needs (invariants, commands, deployment facts) live in `CLAUDE.md`;
  don't copy them here beyond a pointer, so there is one place to keep current.
- Line numbers go stale; name files, types and methods instead.

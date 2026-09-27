# Media pipeline (PC screen → glasses video)

Send-only WebRTC video: GDI capture → NV12 → H.264 (Media Foundation software MFT) → our own
RTP packetizer → pacer → SIPSorcery SRTP on fixed UDP 50000 → the glasses' WebView. VP8
(libvpx) is the fallback where Windows has no H.264 encoder.

## Files

| File | Role |
| --- | --- |
| `server/Media/FramePump.cs` | The per-session loop: tick at `FramesPerSecond` (20), pick source rect, keyframe policy, capture, encode, send, stats window. |
| `server/Windows/GdiCaptureSource.cs` | `CopyFromScreen` of the source rect, bilinear scale into a letterboxed 600×600 BGRA frame. No cursor is captured (the client draws its own). |
| `server/Media/Nv12.cs` | BGRA → NV12 (BT.601 limited range). |
| `server/Media/MfH264Encoder.cs` | Sync software MFT, Baseline, low-latency, CBR, no B-frames, GOP = `KeyframeIntervalSeconds`, mid-stream bitrate changes, forced IDR. |
| `server/Media/SipsorceryMedia.cs` | `Vp8FrameEncoder`, `FrameEncoderFactory`, `SipsorceryMediaPeer` (offer/answer, RTCP handling, NACK resend, `SendPacket`), `MediaPeerFactory`. |
| `server/Media/H264Rtp.cs` | Annex-B access unit → RTP payloads (single NAL or FU-A, marker on the last). |
| `server/Media/RtpPacer.cs` | Dedicated thread; sends at ≥ `PacingKbps` (6000), faster to keep every frame within `MaxPacingDelayMs` (150); urgent queue for resends. |
| `server/Windows/PreciseSleep.cs` | High-resolution waitable timer (sub-ms sleeps for the pacer). |
| `server/Media/SentPackets.cs` | Ring of the last 4096 packets (3 s, one resend per 50 ms, same SRTP epoch only). |
| `server/Media/RtcpNack.cs` | Reads every FCI of every generic NACK in a (decrypted) RTCP packet. |
| `server/Media/BitrateController.cs` | Loss-based target (cut on >10% loss, +8% on <2% while busy), `[MinKbps, TargetKbps]`, starts at `StartKbps`. |
| `server/Media/SdpCandidates.cs` | Offer rewrite: drop LAN candidates (unless enabled), add `PublicIp:MediaPort` as the top host candidate. |
| `server/Media/SdpFeedback.cs` | Adds `nack` (H.264 only), `nack pli`, `ccm fir`, `rtcp-rsize` to the offer. |
| `server/Media/LinkTest.cs` | Diagnostic noise pattern at stepped bitrates (see [stats-and-diagnostics.md](stats-and-diagnostics.md)). |
| `client-web/src/rtc.ts` | `VideoReceiver` (receive-only `RTCPeerConnection`, no ICE servers), `watchFrames` (rVFC). |

## The loop (`FramePump.RunAsync`)

```
wait for peer Connected
every 1/fps (PeriodicTimer: missed ticks coalesce, never a backlog):
  source ← InputController.CurrentSource  (whole monitor in Overview, else the region)
  target ← link test step ?? BitrateController.TargetKbps → encoder.SetTargetKbps on change
  keyframe if: source size changed | PLI/FIR pending and ≥ RequestedKeyframeMinGapMs (1.5 s) since the last | ≥ KeyframeIntervalSeconds (10 s)
  capture (or link-test pattern) → encode → peer.SendFrame(encoded, 90000/fps) → rtp timestamp
  each second: stats window → onStats (mediaStats + stats log), BitrateController.OnSent
```

A region that only *moves* (edge panning) does not force a keyframe; delta frames are
cheaper. The encoder's own GOP follows the same interval.

## Sending H.264 (`SipsorceryMediaPeer`)

- `SendFrame` keeps its own RTP clock (starting at SIPSorcery's random track timestamp),
  packetizes with `H264Rtp` and enqueues the frame on the pacer. VP8 goes through
  SIPSorcery's `SendVideo` unpaced and without NACK support.
- `SendPacket` (pacer thread) takes the next sequence number from the track, stores the
  packet in `SentPackets`, and calls `VideoStream.SendRtpRaw` (SRTP, TWCC extension).
  Resends reuse their original sequence number (plain NACK, no RTX).
- **SRTP rollover:** SIPSorcery advances its rollover counter when it *encrypts* sequence
  65535, so every 65535 must be encrypted exactly once and nothing from before a wrap may be
  resent. `_epoch` counts wraps; `SentPackets` refuses other epochs. Never skip a sequence
  number without sending it. The e2e harness starts streams at 65495 to cross a wrap.

## Feedback from the glasses

| Feedback | How it arrives | Handled in |
| --- | --- | --- |
| Receiver report (loss) + REMB | compound RTCP, parsed by SIPSorcery (`OnReceiveReport`) | `OnReceiverReport` → `FeedbackReceived` → `BitrateController.OnFeedback` (REMB logged only) |
| PLI / FIR | own packet thanks to `rtcp-rsize`; first 8 bytes of SRTCP are clear | `IsStandaloneKeyframeRequest` on the raw channel → `KeyframeRequested` |
| PLI inside a compound report | SIPSorcery keeps only one feedback item per report | still checked in `OnReceiveReport` |
| NACK | own packet; SIPSorcery has already decrypted the channel buffer in place (it subscribed first) | `OnNack` → `RtcpNack.ReadLost` → `SentPackets.TakeForResend` → `RtpPacer.EnqueueUrgent` |

If the buffer is not readable (handler order changed), `OnNack` decrypts a copy with the
stream's SRTCP context. That call updates the same context's replay window as SIPSorcery's
own decrypt, so it matters which one sees a packet first (see the review's SRTCP note).

## Connectivity

- The offer carries one host candidate: the router's public IP and port 50000
  (`Media:PublicIp`, `MediaPort`, must be even). The browser's checks come in through the port
  forward and SIPSorcery learns the browser as peer-reflexive. No STUN/TURN.
- `Media:BindAddress` pins the socket to the Ethernet adapter the router forwards to;
  without it, replies may leave through Wi-Fi and never connect (`ServerApp.WarnIfMultiHomed`).
- The browser's own candidates are mostly mDNS `.local` names; the server can't resolve them
  and ignores them (`AddRemoteCandidate`). The connection forms from the browser's checks.
- One fixed port means one peer at a time; a takeover waits for the old session (and its
  peer) to be disposed before the new peer binds.

## Settings (`Media:*`, `MediaOptions`)

`Codec` (H264/VP8), `FramesPerSecond` 20, `TargetKbps` 2500 (max), `MinKbps` 300,
`StartKbps` 1000, `KeyframeIntervalSeconds` 10, `RequestedKeyframeMinGapMs` 1500,
`PacingKbps` 6000 (0 = no pacing), `MaxPacingDelayMs` 150, `FrameWidth/Height` 600,
`LinkTestOnStart` + steps, `PublicIp`, `BindAddress`, `MediaPort`, `IncludeLanCandidates`.

## History worth knowing (why it is like this)

Bursty keyframes lost ~26% over mobile data → pacing. PLIs inside REMB-carrying reports
vanished → `rtcp-rsize` + raw-header detection. Fixed 2.5 Mbit/s over a ~0.8 Mbit/s relay
queued 1.4 s → loss-based adaptation, 1.5 s keyframe gap, 3 s resend window. A resent 65535
froze video for good → epochs. Details in CLAUDE.md "Gotchas already paid for".

## Tests

`tests/Media/FramePumpTests.cs`, `MfH264EncoderTests.cs` (real encoder), `H264RtpTests.cs`,
`RtpPacerTests.cs`, `SentPacketsTests.cs`, `RtcpNackTests.cs`, `SdpFeedbackTests.cs`,
`BitrateControllerTests.cs`, `Nv12Tests.cs`. e2e: video live, NACK resend, lost-for-good →
PLI, lost stream start → keyframe, sequence wrap on every run.

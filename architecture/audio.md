# Audio (the PC's sound on the glasses)

Whatever the PC plays (every app, at full level whatever the PC's volume) goes to the glasses
as Opus on a second WebRTC track, bundled with the video on the same UDP port. It's secondary
to the video: the glasses' ♪ button turns it off (and the PC stops capturing and sending), and
nothing turns it off automatically. The status bar shows what video and audio each use.

## Files

| File | Role |
| --- | --- |
| `server/Windows/ProcessLoopbackCapture.cs` | **Preferred** (Windows 10 2004+): process loopback through the virtual `VAD\Process_Loopback` device, exclude mode (every app but the server), 48 kHz stereo float. Taken before the PC's master volume and mute and whatever device each app plays on, so the glasses get full level and the user sets loudness there. Small interop for `ActivateAudioInterfaceAsync` (NAudio has it internally, not public). |
| `server/Windows/LoopbackAudioCapture.cs` | Fallback: WASAPI endpoint loopback of the default render device (NAudio.Wasapi); follows the PC's volume and mute. Asks Windows for 48 kHz stereo float (`AUTOCONVERTPCM`); otherwise takes the mix format (float or 16-bit) and converts. Polled, 100 ms Windows buffer; `IsStale` checks the default device once a second. `LoopbackAudioCaptureFactory` tries process loopback first (falls back for good if activation fails), logs which, or returns null (no output device). |
| `server/Desktop/Abstractions.cs` | `IAudioCapture`, `IAudioCaptureFactory`, `AudioFormat48k` (48 kHz, 2 channels). |
| `server/Desktop/AudioFifo.cs`, `AudioConverter.cs` | Stereo sample FIFO; linear resampler and channel fold (centre kept) for the fallback path. |
| `server/Media/AudioTimeline.cs` | Wall-clock framing: a frame is due when its time has passed; captured sound is used once a frame + 10 ms is buffered, trimmed above 80 ms, silence otherwise; skips ahead after a > 10-frame stall. Positions are the RTP timestamps (less a random base). |
| `server/Media/OpusAudioEncoder.cs` | Concentus (already a SIPSorcery dependency): 48 kHz stereo, `OPUS_APPLICATION_AUDIO`, `Audio:Kbps` (40) VBR, in-band FEC, complexity 5 (~3% of a core), `PacketLossPercent` from the glasses' loss. `IsDtx`, `IsDigitalSilence`. |
| `server/Media/AudioPump.cs` | Per session, own thread (5 ms `PreciseSleep`): capture → timeline → Opus → `IMediaPeer.SendAudio`. `Enabled` (starts off), `Capturing`, `TakeStats` (`On`, payload `Kbps`, `Packets`). Reopens the capture on device change or failure (2 s retry). |
| `server/Media/SipsorceryMedia.cs` | With `Audio:Enabled` the peer adds a send-only Opus track (`OpusParameters`: `minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;usedtx=1`, PT 111). `SendAudio` sends at once under `_sendLock` (shared with the pacer's video sends). |
| `server/Media/SdpStreams.cs` | Separate `a=msid` per track (`pc-audio`, `pc-video`), so the browser doesn't lip-sync them. |
| `server/Sessions/ControlSession.cs` | Creates the pump when the peer carries audio; `hello.audio`; `setAudio`; audio figures in `mediaStats` and the `pc` log line; `setAudio` events; `CastArea.SetAudio`. |
| `server/Desktop/CastArea.cs`, `server/Ui/SessionBanner.cs`, `TrayApp.cs` | "♪ sound on" in the PC's session banner. |
| `client-web/src/audio.ts` | ♪ setting in localStorage (`glasses.audio`, default on), `withStereoOpus` (answer fix-up), `bandwidthLabel`. |
| `client-web/src/audioOutput.ts` | `AudioOutput`: plays the stream through Web Audio (an `AudioContext`, gain 0 when off); the `<audio>` element stays, muted, because Chrome only pulls a remote WebRTC track into Web Audio while an element plays it. `resume()` never settles without a gesture, so it's raced against 300 ms. |
| `client-web/src/rtc.ts` | `ontrack` routes by kind: the audio stream → `AudioOutput.attach`, video → `<video>`; applies `withStereoOpus` to the answer; audio inbound-rtp snapshot → `audioStats`. |
| `client-web/src/SessionScreen.tsx` | `<audio>` element, ♪ button (only when `hello.audio`; always just `♪`, highlighted when on, warning colour while blocked, `data-output` = the AudioContext state), `playAudio` with the gesture fallback, `setAudio` after `hello` and on toggle, `V n · A n kbps` in the status bar. |

## Flow

```
PC                                                     glasses
hello{…, audio:true}  ───────────────────────────────►  shows ♪ (setting from localStorage)
                      ◄───────────────────────────────  setAudio{enabled}
AudioPump.Enabled = true: open process loopback (else endpoint loopback)
every 5 ms: drain WASAPI → AudioFifo → AudioTimeline
  due 20 ms frame → Opus → SendAudio (at once, no pacer)
RTP (SSRC audio, PT 111, 48 kHz clock) ─── UDP 50000 ──►  AudioContext (muted <audio> keeps it flowing)
mediaStats{…, audioOn, audioKbps, audioPackets} ──────►  panel "PC n kbps"; status bar from getStats
```

## Rules and why

- **Off until asked, and off means off.** The pump starts disabled; the glasses send their
  setting right after `hello`. While off nothing is captured or sent (`Capturing` false).
  `setAudio` counts as input and is logged as an event only when it changes something.
- **No renegotiation.** The audio track is always in the offer when `Audio:Enabled`; toggling
  only starts or stops the pump. `Audio:Enabled=false` gives a video-only offer and no ♪.
- **Audio never waits for video.** `SendAudio` goes straight out, not through `RtpPacer`
  (a keyframe can sit there up to 150 ms). Video and audio share one SRTP transport, so both
  encrypt under `_sendLock`.
- **SRTP rollover:** audio has its own SSRC and rollover counter. Every packet takes the next
  sequence number and is encrypted exactly once (never resent, never skipped), which is what
  SIPSorcery's counting needs (see the video's gotcha in CLAUDE.md). At 50 packets/s it wraps
  every ~22 min; the e2e harness starts audio 300 packets before its wrap.
- **No lip sync (`SdpStreams`).** Tracks in one stream get synchronised by the browser, which
  would delay the video to match the sound, from SIPSorcery's sender reports that don't follow
  our video RTP clock. Video latency comes first; the sound may lead the picture a little.
- **Silence (DTX).** Opus's own DTX only exists in its speech modes; in general-audio (CELT)
  mode digital silence is still a 3-byte packet every 20 ms. So the pump sends two silent frames,
  then a header-only packet (1 byte) when silence starts and every 400 ms, nothing in between.
  Chrome reads ≤ 2-byte Opus packets as DTX and plays silence; with plain gaps it concealed them
  as lost (~400 ms "concealed" per silent second in the harness before this).
- **Stereo:** Chrome decodes Opus in mono unless its *own* description has `stereo=1`, which it
  never adds to its answer; `withStereoOpus` adds it before `setLocalDescription`.
- **Autoplay:** browsers start sound only after a user gesture. After Pair/Reconnect it plays at
  once; after a page reload (auto-resume) the AudioContext may stay suspended, ♪ turns the
  warning colour, and the next pinch or swipe (`pointerdown`/`keydown`, capture phase) retries.
  The button never changes its label: a wider one pushed the top bar past the display.
- **Full level.** Process loopback records before the PC's volume: measured at 22 % (-22.9 dB),
  a -34 dBFS tone came back at -34 dBFS, where endpoint loopback got it 22.9 dB quieter (that
  was why the first try on the glasses sounded silent). Only the endpoint fallback follows the
  PC's volume and mute.
- **Loss → FEC.** The glasses' video receiver reports (same path) set Opus's
  `PacketLossPercent`, which spends more of each packet on FEC for the previous one.
- **What reaches the glasses:** only the sound the PC plays. No microphone anywhere.

## Bandwidth

`Audio:Kbps` 40 (stereo, VBR) is ~40 kbit/s of payload while sound plays; with RTP/SRTP/UDP/IP
headers at 50 packets a second, ~55-60 kbit/s on the wire. Silence costs ~20 bit/s of payload
(one 1-byte packet per 400 ms). `Audio:FrameMs` 40 or 60 would cut the header share at the
cost of that much delay. The status bar and panel show payload (like the video's `kbps`).

## Settings (`Audio:*`, `AudioOptions`)

`Enabled` (true), `Kbps` (40), `FrameMs` (20; 10/20/40/60).

## Tests

`tests/Media/AudioPumpTests.cs` (off until on, 20 ms packets on the 48 kHz clock, markers, DTX,
not before connect, no device), `AudioTimelineTests.cs`, `OpusAudioEncoderTests.cs` (real
Concentus: decode round trip in stereo, bitrate, silence, CPU), `SdpStreamsTests.cs` (the real
offer: Opus fmtp, msids, feedback on video only), `tests/Desktop/AudioConverterTests.cs`,
`RtcpReadableTests` (reports on the audio SSRC), `ControlProtocolTests` (`setAudio`),
`EndpointTests.Sound_goes_out_only_while_the_glasses_have_it_on_and_is_logged`;
`client-web/src/audio.test.ts`, `mediaStats.test.ts` (`audioStats`), `protocol.test.ts`. The
harness plays a beeping tone (`ToneCaptureFactory`); e2e checks: sound by default, ♪ off/on,
events, sequence wrap, setting remembered across a restart.

Checked by hand on this PC: both loopbacks open; endpoint loopback delivers 48,000 frames a
second while anything plays and nothing while silent; the full-level measurement above.
`tests/Windows/ProcessLoopbackCaptureTests.cs` opens process loopback (silently) on every run.

**On the glasses (2026-09-27):** the sound plays through the glasses' speakers via Web Audio,
with the glasses' own volume. Not verified: the gesture fallback after a reload; whether the
plain `<audio>` element would also have played (the first silent try was the PC's volume).

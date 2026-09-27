# End-to-end harness (dev only)

Runs the **real** server pipeline (Kestrel, WebSockets, pairing, SIPSorcery WebRTC,
H.264 via Media Foundation, GDI screen capture) with these changes, so a headless browser
can drive the whole flow unattended:

1. pairing requests are **approved automatically**;
2. mouse and keyboard input and app switches are **recorded, never applied** to your desktop;
3. every stream starts just below a sequence-number wrap, and `/__harness/lose-*` endpoints
   can drop a stream's start or single packets (to exercise PLI and NACK).

It listens on `127.0.0.1:5081` only. Never deploy it or expose it.

## Run

```powershell
# build the client once
cd client-web; npm install; npm run build; cd ..

# terminal 1: the harness
dotnet run --project tools/e2e-harness

# terminal 2: drive the real client in headless Chrome
cd tools/e2e-harness/browser
npm install
npm run drive                       # add -- --headed to watch, -- --screenshots <dir> to save frames
```

The script runs about 37 checks: pairing, live video, brightness and look, the Stats panel
and stats log, pointer drag, click and double-click, typing (flattened, Enter separate),
Region mode, edge lock, edge scroll and panning, focus navigation and Back, the Type round
trip, app buttons and swipes, scroll strength, NACK resends, keyframe requests, resuming
after a reload, a hidden app ending the session, and Reconnect after the PC ends it. See
`browser/drive.mjs` for the exact list. Restart the harness between runs (it keeps recorded
input for its whole life). More in `architecture/testing.md`.

Note: screen capture is real, so the video (and any screenshots) show your actual
screen. Everything stays on this PC.

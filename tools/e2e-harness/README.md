# End-to-end harness (dev only)

Runs the **real** server pipeline (Kestrel, WebSockets, pairing, SIPSorcery WebRTC,
VP8, GDI screen capture) with two changes, so a headless browser can drive the
whole flow unattended:

1. pairing requests are **approved automatically**;
2. mouse and keyboard input is **recorded, never injected** into your desktop.

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

The script checks, in order:
1. pairing leads into a session;
2. WebRTC video is live;
3. pointer drag moves the cursor and a short tap clicks;
4. a scroll drag sends wheel input;
5. typed text arrives flattened and without Enter, and Enter is a separate key;
6. Use region returns to View mode;
7. View mode ignores taps.

Note: screen capture is real, so the video (and any screenshots) show your actual
screen. Everything stays on this PC.

# M0 – WebRTC + network spike

Throwaway app that answers one question before anything else gets built: does
WebRTC video reach the client through the router's port forward, fast enough?

It streams a synthetic 600×600 test pattern, with no desktop capture, input or
auth. It is safe to expose publicly while testing.

## How it works

- ASP.NET Core on `127.0.0.1:5080` serves the test page and a plain WebSocket at `/ws/spike`.
- SIPSorcery creates a send-only video track, bound to the fixed UDP port **50000**.
- The SDP offer carries a host candidate for the router's **public IP** on that port
  (`Spike:PublicIp`). The browser sends its checks there, the router forwards them,
  and SIPSorcery learns the browser's address as a peer-reflexive candidate.
  No STUN or TURN is involved.
- Every frame carries the server clock as a black/white barcode across the top.
  The page reads it back from the decoded pixels to measure end-to-end latency.
- Encoder: VP8 (libvpx, bundled with `SIPSorceryMedia.Encoders`). H.264 comes later,
  behind the encoder interface in the real server.

## Run locally (LAN)

```powershell
cd spikes/webrtc
dotnet run --launch-profile spike
# open http://127.0.0.1:5080/  (or http://<pc-lan-ip>:5080 with --urls)
```

## Run through the router (the real test)

1. Router and DNS are set up as described in the plan's Setup section (TCP 443 and UDP 50000 forwarded).
2. Set your public IP and turn off LAN candidates, so the only route is the port forward:

   ```powershell
   $env:Spike__PublicIp = "<your static IP>"
   $env:Spike__IncludeLanCandidates = "false"
   dotnet run --launch-profile spike
   ```

3. In another terminal, from the repo root: `caddy run --config deploy/Caddyfile.spike`
4. On the laptop, **tethered to the phone's mobile data**, open
   `https://glasses.example.com/`.
5. The HUD shows latency (p50/p95), fps, decode time, codec and the ICE path.
   The path should end in `<your static IP>:50000 (host)`.

## Automated check

`e2e/check.mjs` drives the installed Chrome in headless mode and exits 0 on PASS:

```powershell
cd spikes/webrtc/e2e
npm install
npm run check -- http://127.0.0.1:5080/ --seconds 15
```

## Results so far

| Date | Setup | Path | Latency p50 / p95 | FPS | Codec |
| --- | --- | --- | --- | --- | --- |
| 2026-09-24 | Chrome on the same PC, LAN candidates | host → host | 24 / 34 ms | 31 | VP8 |
| 2026-09-24 | Chrome on the same PC, public candidate only (127.0.0.1 stand-in) | prflx → host :50000 | 19 / 30 ms | 30 | VP8 |
| _to do_ | Laptop on phone hotspot via router | | | | |
| _to do_ | Meta Ray-Ban Display | | | | |

Same-machine numbers only prove the pipeline works. The hotspot run is the real M0 exit test.

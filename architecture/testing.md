# Testing

Three layers: server xUnit tests (unit + in-memory integration), client Vitest unit tests,
and an end-to-end harness that drives the real client in headless Chrome against the real
server pipeline.

## Server tests (`tests/`, ~177, ~19 s)

| Folder | What |
| --- | --- |
| `Pairing/` | Coordinator state machine, device grants, secrets (with `FakeTimeProvider`). |
| `Protocol/` | Every message shape, caps, flattening, stats fields. |
| `Sessions/` | `InputController` mode gating and mapping. |
| `Hosting/` | `EndpointTests` (full socket flows on `TestServerHost`), origin policy, same-origin flag, caching. |
| `Media/` | Pump, real MF H.264 encoder, RTP packetizer, pacer, NACK parsing, resend ring, SDP, bitrate, NV12. |
| `Desktop/`, `Alerts/` | Region maths, cast area, alert throttle. |
| `Fakes/` | `FakeInput`, `FakeScreen` (2560×1440), `FakeKeepAwake`, `FakeWindowSwitcher`, `FakePeer` (connect, PLI, feedback), `FakeEncoderFactory`, `FakeCapture`. |

`TestServerHost` builds the real app with `ServerApp.Create` on a `TestServer`, swaps the
desktop and media for fakes, points region/stats/grant files at fresh temp paths, captures
logs (for `Tokens_never_appear_in_logs`) and offers helpers such as `StartSessionAsync`.

**Gotcha:** the test project copies the real `server/appsettings.Local.json` into its output,
and `ServerApp.Create` loads it, so the PC's app shortcuts leak into tests
(`App_shortcuts_come_from_the_pc_config…` fails). Delete it from the output folder before
running (`dotnet test <dll>` after deleting avoids a rebuild re-copying it).

## Client tests (`client-web`)

`npm test` (Vitest, node environment, `src/**/*.test.ts`, ~84), `npx tsc --noEmit`,
`npm run build`. Only pure modules are unit-tested; React components are covered by e2e.

## End-to-end harness (`tools/e2e-harness`)

`Program.cs` runs `ServerApp.Create` on 127.0.0.1:5081 with:
- pairing auto-approved (`RequestOpened` → `Approve`), DEV ONLY;
- `RecordingInput` / `RecordingSwitcher` instead of `SendInput` / real window moves;
- `NoKeepAwake`; temp region/stats/grant files; two fake app shortcuts;
- `HarnessPeers`: real SIPSorcery peers that start every stream near a sequence wrap and can
  drop the next stream's start or one packet (`/__harness/lose-*`).
- Inspection endpoints: `/__harness/input`, `/session`, `/region`, `/terminate`.

`browser/drive.mjs` (puppeteer-core + local Chrome, `CHROME_PATH` overrides) runs ~37 checks:
pairing, live video, brightness/look, stats panel and stats log, pointer drag/tap/double-click,
typing, region, edge lock/scroll/pan, focus navigation and Back, Type round trip, app buttons
and swipes, scroll strength, NACK resend, PLI, lost stream start, resume after reload, hidden
app ending the session, Reconnect after a PC terminate. Press top-bar buttons with `tapBar()`.

Run it (restart the harness between runs; it keeps recorded input for its whole life):

```powershell
dotnet build tools/e2e-harness -p:OutDir=E:/_src-unity/MetaDisplayRDP/tools/e2e-harness/bin/isolated/
# delete tools/e2e-harness/bin/isolated/appsettings.Local.json (its bind address breaks the harness)
$env:Media__MediaPort=50002; ./tools/e2e-harness/bin/isolated/E2eHarness.exe
cd tools/e2e-harness/browser; npm run drive
```

## What needs the real device

Anything about the Neural Band's actual events, the composer, the WebView's decoder, the
phone relay link, the router and the public path. Say plainly when a change wasn't verified
there.

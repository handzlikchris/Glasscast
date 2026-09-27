# Pairing and authentication

Who may start a session, and how. Nothing reaches the desktop without a human clicking
**Approve** on the PC, or a device token that came out of such an approval less than 24 h ago.

## Files

| File | Role |
| --- | --- |
| `server/Pairing/PairingCoordinator.cs` | The state machine, `PairingRequest`, `SessionLease`, device-grant logic. Everything under one lock (`_gate`). |
| `server/Pairing/DeviceGrant.cs` | `DeviceGrant` record + `DeviceGrantStore` (hashes in `%LOCALAPPDATA%\GlassesRemote\device-grant.json`). |
| `server/Pairing/Secrets.cs` | Pairing codes, ids, 256-bit tokens, SHA-256 hashing, constant-time compare. |
| `server/Pairing/SlidingWindowLimiter.cs` | Pairing rate limits (per IP and global). |
| `server/Pairing/PairingOptions.cs` | `Pairing:*` settings (timeouts, limits, grant lifetime/file, takeover timeout). |
| `server/Hosting/GlassesEndpoints.cs` | `/ws/pair` (`PairAsync`), the auth step of `/ws/session` (`SessionAsync`), `OriginPolicy`. |
| `server/Alerts/AlertLog.cs`, `AlertThrottle.cs` | Probe signals → recent list + throttled Windows notifications (see [pc-ui.md](pc-ui.md)). |
| `server/Ui/ApprovePopup.cs` | The Approve/Reject window. |
| `client-web/src/connection.ts` | Client side: pair socket, holds the approval token in module memory, device token in localStorage. |
| `client-web/src/PairingScreen.tsx`, `App.tsx` | Code display, retry, "remembered → go straight to a session". |

## State machine (`PairingCoordinator`)

```
            TryOpenRequest            Approve                 TryAuthenticate
   Idle ─────────────────► Pending ─────────► TokenIssued ─────────────────► ActiveSession
    ▲  ◄── Reject/Expire/Cancel ─┘   │                                           │
    │  ◄──────── ExpireToken (TokenUseWindow, 30 s) ─┘                           │
    └────────────────────── EndSession (lease disposed) ◄───────────────────────┘
                    TryResumeAsync (device token) : Idle ──────────────────► ActiveSession
```

- Exactly one pending request, one issued token and one active session at a time. Anything
  arriving in the wrong state gets `null`, which the endpoint turns into the generic
  `pairFailed` / `authFailed` (callers must never say why).
- `TryOpenRequest` checks the per-IP limit (5/min) and the global limit (20/min) before the
  state, raises `PairingRateLimited` / `PairingWhileBusy` alerts, starts a `RequestTimeout`
  (60 s) timer and fires `RequestOpened` (the tray shows the popup).
- `Approve` creates the approval token, keeps only its hash, starts the `TokenUseWindow`
  timer (30 s) and completes the request's `Outcome` with the raw token; `PairAsync` sends
  it as `paired` on that socket only and closes it.
- `TryAuthenticate` compares hashes in constant time, clears the token (single use), creates
  a fresh `DeviceGrant` (replacing any other remembered device) and returns a
  `SessionLease` that carries the first device token.

## Device grants (resume without the popup)

- A grant is `{Id, CurrentHash, PreviousHash, ExpiresAt}`. `ExpiresAt` is fixed at approval
  time (`DeviceGrantLifetime`, 24 h; 0 turns the feature off) and never extended.
- Every successful `TryResumeAsync` swaps the token: `PreviousHash ← CurrentHash`,
  `CurrentHash ← hash(new)`. The new token goes to the glasses once, in `authenticated`
  (`SessionLease.TakeDeviceToken` hands it out and drops it).
- Presenting the **previous** token = a copy exists somewhere: the grant is cleared,
  `DeviceTokenReused` is raised and the active session is terminated.
- Takeover: if the same grant already owns the active session (a dropped connection the
  server hasn't noticed), the old lease is `Signal()`ed and marked `Superseded`; the resume
  waits up to `TakeoverTimeout` (5 s) for it to release, then retries once. It never takes
  over another device's session and never runs while a pairing is pending.
- The grant is forgotten by: tray **Forget remembered glasses**, a protocol violation in the
  session (`SessionLease.ForgetDevice`), token reuse, expiry. Ending a session on the PC
  (tray / hotkey) keeps it (decision of 2026-09-27).
- `DeviceGrantStore` persists hashes only, so a server restart keeps the device remembered.

## Client side (`connection.ts`)

- `requestPairing` opens `/ws/pair`, shows `pairCode`, stores the token from `paired` in the
  module-level `heldToken` (never React state, URL, storage or log).
- `Session.open` sends `authenticate` with `heldToken` (then drops it), else `resume` with
  the stored device token, else throws "Not paired".
- On `authenticated` with a device token → `saveDevice` (localStorage key
  `glassesRemote.device`, with `expiresAt`). On `authFailed` while resuming → `forgetDevice`.
  On close reasons `invalid message` / `rate limit` / `bad answer` → `forgetDevice` (the PC
  forgot the device too).
- `App` starts in a session when `isDeviceRemembered()`; the ended screen shows
  **Reconnect** (focused) only when a device token is still stored.

## Rules (see also CLAUDE.md "Security invariants")

- No auto-approve, bypass flag or network approval endpoint in `server/`. Auto-approval
  lives only in `tools/e2e-harness`.
- Tokens: 256-bit, hashed server-side, constant-time compare, length-checked (≤128 chars)
  before hashing, redacted in `ToString()` of `AuthenticateMessage`/`ResumeMessage`, never
  logged (`Tokens_never_appear_in_logs`).
- Pairing codes are display-only (`ABC-123`, no 0/O/1/I/L), never a credential.
- The Origin check (`OriginPolicy`) only stops *browsers* on other sites. Any internet client
  can send any Origin, so the rate limits and the human approval are the real gate.

## Known gaps

- If the connection drops after the server swapped the device token but before
  `authenticated` arrives, the glasses retry with the old token and are treated as a stolen
  copy (device forgotten, alert raised). See the code review.
- A resume refused because the server is momentarily busy (a pairing pending, a takeover
  timing out) comes back as `authFailed`, and the client then deletes a device token that
  is still valid.

## Tests

`tests/Pairing/PairingCoordinatorTests.cs` (state machine, limits, timeouts, concurrency),
`tests/Pairing/DeviceGrantTests.cs` (swap, reuse, expiry, persistence, takeover, forget),
`tests/Pairing/SecretsTests.cs`, `tests/Hosting/EndpointTests.cs` (sockets end to end, generic
failures, origins, log hygiene). e2e: "restarting the page reconnects without pairing",
"after the PC ends a session, a pinch presses Reconnect…".

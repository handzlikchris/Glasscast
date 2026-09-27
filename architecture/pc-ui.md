# PC user interface (tray, popup, banner, frame)

Everything the person at the PC sees. It runs on the STA thread in `Program.Main`
(`Application.Run(tray)`); the web host runs on thread-pool threads. Events from the
coordinator, alert log and cast area are marshalled onto the UI thread with `BeginInvoke`
(`TrayApp.Ui`), so raising them never blocks the caller.

## Files

| File | Role |
| --- | --- |
| `server/Program.cs` | DPI init, start the web host, run the tray; either side exiting stops both. |
| `server/Ui/TrayApp.cs` | `ApplicationContext`: tray icon (grey idle, amber pairing, green session), menu, popup lifecycle, banner, frame, notifications; `TerminateHotkey`; `TrayIcons`. |
| `server/Ui/ApprovePopup.cs` | Code, source IP, countdown; Reject is focused and the Cancel button, no AcceptButton; closing = reject. |
| `server/Ui/SessionBanner.cs` | Top-centre bar "Glasses in control from … – Ctrl+Alt+Shift+X to end"; no-activate, excluded from capture. |
| `server/Ui/CastFrame.cs` | Orange 3 px frame just outside the cast area; click-through, no-activate, transparency key, excluded from capture. |
| `server/Ui/AlertsForm.cs` | Recent alerts list (newest first, 200 kept). |
| `server/Alerts/AlertThrottle.cs` | At most one balloon per minute; the rest are counted into the next one (flushed every 10 s). |

## Tray menu

Status line · **End session (Ctrl+Alt+Shift+X)** · **Forget remembered glasses (until …)** ·
**Show cast area on screen** (toggle) · **Recent alerts…** · **Exit**.

- End session → `PairingCoordinator.TerminateActiveSession` (the glasses stay remembered).
- Forget → `ForgetDevice`; the active session, if any, carries on.
- The hotkey is registered globally with `MOD_NOREPEAT`; if another app owns it, a balloon
  says so and the menu is the only way.

## Rules

- The popup must never approve on Enter/Space/Esc: Reject is focused and is the Cancel
  button; `AcceptButton = null`.
- Banner and frame use `WDA_EXCLUDEFROMCAPTURE` and never take focus (they would otherwise
  steal keyboard input from the app the glasses are driving).
- UI code touches WinForms only on the UI thread; add new coordinator events through `Ui(...)`.

## Tests

`tests/Alerts/AlertThrottleTests.cs`, `tests/Desktop/CastAreaTests.cs`; the forms themselves
are not tested (manual).

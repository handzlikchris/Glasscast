# Task: a smaller keyboard on the square screen

Status: **idea, not started** (written 2026-09-29 at the user's request, for later). Read
`CLAUDE.md` first, then `architecture/phone-mode.md` and `android-companion/README.md`.

## The problem

For phone mode the user runs the S25 as a square screen: 1080×1080 at density 320, switched with
**Square screen** / **Reset screen** on the companion's setup screen (`DisplayOverride.kt`). The
glasses show the whole square, which works well. But the phone's keyboard takes a large part of it:

- **Samsung Keyboard** ignores the square override and density: its height seems to follow the
  physical screen, so it filled most of the square. Its own size settings don't go small enough.
- **Gboard** (now the default, `com.google.android.inputmethod.latin`) follows the density and
  is better, but still tall. It can go smaller by hand: toolbar → Resize (drag the top handle
  down), or Floating (shrink by the corners), plus Preferences → Number row off and Text correction
  → Show suggestion strip off.
- The user wants the **smallest keyboard on the square screen and the normal one otherwise**,
  without setting it by hand each time. Down twice on the glasses (the phone's Back) already puts
  a keyboard away.

## What we know

- The companion **can't resize Gboard**: Gboard keeps its size in its own private storage, and no
  other app can change it without root.
- The companion **can switch the keyboard**: `Settings.Secure.DEFAULT_INPUT_METHOD` (and
  `ENABLED_INPUT_METHODS`) are secure settings, and the companion already holds
  `WRITE_SECURE_SETTINGS` (granted once over adb for the square screen).
- Typing from the glasses doesn't need a visible keyboard: the Type panel's text goes into the
  focused field through the companion's accessibility service (as a keyboard where it can,
  `flagInputMethodEditor`; else `ACTION_SET_TEXT`).

## Options to explore

1. **Try other keyboards first (no code):** one that sizes small and remembers it, e.g. Microsoft
   SwiftKey (resizes freely), FUTO Keyboard, Unexpected Keyboard (open source). Check how each
   behaves at 1080×1080 / density 320.
2. **Switch keyboards with the square screen:** on the setup screen, a "Keyboard on the square
   screen" picker listing the enabled keyboards (`InputMethodManager.getEnabledInputMethodList`).
   Square screen remembers the current keyboard and switches to the chosen one; Reset screen puts
   the remembered one back. Pairs with option 1 (a second keyboard set up small once) or option 3.
3. **An invisible "glasses keyboard" in the companion:** an `InputMethodService` whose input view
   is empty (or a thin strip with Enter and ⌫). Apps see a keyboard as open (so apps that only
   take key events while their keyboard is up still work), nothing covers the screen, and text
   comes from the glasses. Android shows its standard "this keyboard may collect what you type"
   warning when it's first enabled; say plainly that it sends nothing anywhere.

## Things to check

- Whether the accessibility service's input connection still reaches the focused field when our
  own IME is the current one (it should; test with a normal text field and with Microsoft's
  Windows App, which only takes keyboard input while its keyboard is open).
- Switching IME while a field is focused: does the new one show at once, or only on next focus?
- Security: a keyboard sees what's typed. Ours must never log or send text; say so in the README
  and the security invariants (CLAUDE.md, phone mode).
- Keep `swipes.ts` / the ? panel (`shortcuts.ts`) in mind if any glasses gesture changes.

## Done when

On the S25: Square screen → a small (or no) keyboard during a glasses session; Reset screen → the
user's normal keyboard at its normal size, with nothing to set by hand. Tested on the device,
README and phone-mode.md updated.

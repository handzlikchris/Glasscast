// Claude (the Android app): down twice puts the cursor on the message box; from there the
// swipes step along the row under it (architecture/app-profiles.md, "Claude: the first map").
// A pinch held still for 1 s presses the microphone; held again, Send (not Stop: what was said
// goes straight away; user's choice, 2026-11-12).
// Matchers from a UI dump on the S25 (2026-10-05): Jetpack Compose, so no resource ids; labels
// are English, on the icons inside the buttons; the model picker has none of its own.
import type { AppProfile } from './profile';

export const CLAUDE: AppProfile = {
  name: 'Claude',
  packages: ['com.anthropic.claude'],
  gestures: {
    doubleDown: { highlight: 'box' },
  },
  // Taking turns, not reading the screen: the microphone's label isn't always "Start speech
  // input" (a second try went straight to Send, 2026-11-12), so the glasses count the holds.
  hold: { steps: [['mic'], ['send']], says: 'microphone · held again: send' },
  whileHighlighted: 'map',
  controls: {
    box: [{ kind: 'field', pick: 'lowest' }],
    attach: [{ label: /^Add context$/i }],
    model: [{ between: ['attach', 'mic'] }, { between: ['attach', 'send'] }],
    mic: [{ label: /^Start speech input$/i }, { label: /^(?!stop).*(speech|dictat|microphone)/i }],
    // Send, there even with the box empty. Never Stop: the hold sends what was dictated.
    send: [{ label: /^Send\b/i }],
  },
  names: { box: 'message box', attach: '+', model: 'model picker', mic: 'microphone', send: 'send' },
  moves: {
    // + is under the box in a chat, left of it while Claude works (both seen on the S25).
    box: { down: 'attach', left: 'attach', right: ['mic', 'send'] },
    attach: { up: 'box', right: ['model', 'mic', 'send'] },
    model: { up: 'box', left: 'attach', right: ['mic', 'send'] },
    mic: { up: 'box', left: ['model', 'attach'], right: 'send' },
    send: { up: 'box', left: ['mic', 'model', 'attach'] },
  },
  hint: 'down twice: the message box (then down: +, right: model, microphone, send)',
  notes: [
    { gesture: 'highlighted: swipes', action: 'step: message box ↓ + → model → microphone → send' },
    { gesture: 'pinch the message box', action: 'Type (composer, Send text, Send)' },
    { gesture: "the phone's Back", action: 'Back on the bar' },
  ],
};

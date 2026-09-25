// Where swipes and pinches go on the glasses.
//
// On Meta Ray-Ban Display a swipe arrives as an arrow key and a pinch as a pointer
// tap at the pointer's position, which is usually over the full-screen gesture layer
// rather than a button. So the app keeps a navigation target:
//
//   "view"     swipes move the view around the monitor by half a screen; pinches do
//              whatever the mode does (click in Pointer mode). Used in View, Pointer
//              and Scroll modes, which have no panels of their own.
//   "controls" swipes move focus between buttons and a pinch presses the focused one.
//
// Pinch, then pinch and hold (see TapThenHold) jumps from "view" to "controls".
// Picking View, Pointer or Scroll goes back to "view". The glasses may also send a
// pinch as Enter; whichever arrives second is dropped so one pinch never presses twice.

import type { ViewMode } from './protocol';

export type NavTarget = 'view' | 'controls';

/** Modes where swipes move the view. Overview and Type have their own buttons to reach. */
export const VIEW_NAV_MODES: readonly ViewMode[] = ['view', 'pointer', 'scroll'];

/** Where swipes go after switching to `mode`. */
export function navAfterMode(mode: ViewMode): NavTarget {
  return VIEW_NAV_MODES.includes(mode) ? 'view' : 'controls';
}

/** A pointer tap and an Enter key this close together are the same pinch. */
export const SAME_PINCH_MS = 500;

export type TapRoute = 'pressFocused' | 'mode' | 'ignore';

export interface TapContext {
  nav: NavTarget;
  /** A button or text box in the app holds the remembered focus. */
  hasFocused: boolean;
  /** Time since the last Enter key, in ms (Infinity if none). */
  msSinceEnter: number;
}

/** What a tap on the gesture layer should do. */
export function routeTap(c: TapContext): TapRoute {
  if (c.msSinceEnter < SAME_PINCH_MS) return 'ignore';
  if (c.nav === 'controls' && c.hasFocused) return 'pressFocused';
  return 'mode';
}

/** An Enter key right after pointer activity is the same pinch; the pointer path owns it. */
export function enterIsSamePinch(msSincePointer: number): boolean {
  return msSincePointer < SAME_PINCH_MS;
}

/** Arrow keys as view steps (-1, 0 or 1 per axis). */
export const ARROW_STEPS: Readonly<Record<string, { dx: number; dy: number }>> = {
  ArrowLeft: { dx: -1, dy: 0 },
  ArrowRight: { dx: 1, dy: 0 },
  ArrowUp: { dx: 0, dy: -1 },
  ArrowDown: { dx: 0, dy: 1 },
};

// Where swipes and pinches go on the glasses.
//
// On Meta Ray-Ban Display a swipe arrives as an arrow key and a pinch as a pointer
// tap at the pointer's position, which is usually over the full-screen gesture layer
// rather than a button. So the app keeps a navigation target:
//
//   "view"     swipes act on the view (see swipeAction); pinches do whatever the mode
//              does (click in Pointer mode). Used in View, Pointer and Scroll modes, which
//              have no panels of their own.
//   "controls" swipes move focus between buttons and a pinch presses the focused one.
//
// Back (middle-finger pinch) jumps from "view" to "controls" and back again; pinch, then
// pinch and hold (see TapThenHold) also jumps to "controls".
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

/**
 * Which mode button pinch-then-hold focuses: the likely next step, so it's one pinch away.
 * From Pointer that's Type; from View or Scroll it's back to Pointer.
 */
export function menuFocusFor(mode: ViewMode): ViewMode {
  switch (mode) {
    case 'pointer':
      return 'type';
    case 'view':
    case 'scroll':
      return 'pointer';
    default:
      return mode;
  }
}

/**
 * The glasses deliver Back as history.back() (when the page has an entry to go back to) and
 * possibly also as an Escape/Backspace key. Two Backs this close together are one gesture.
 */
export const SAME_BACK_MS = 400;

/**
 * Where Back takes you: from the view up to the controls; from the controls (and so from Type
 * and Overview, which live there) home to Pointer mode.
 */
export function backTarget(nav: NavTarget): 'controls' | 'pointer' {
  return nav === 'view' ? 'controls' : 'pointer';
}

/** Wheel units one swipe scrolls by (three notches). */
export const SWIPE_SCROLL = 360;

export type SwipeAction =
  | { kind: 'pan'; dx: number; dy: number }
  /** Browser deltaY sign: positive scrolls down. */
  | { kind: 'scroll'; dy: number }
  | { kind: 'type' }
  | { kind: 'nextApp' };

/**
 * What a swipe does while swipes are on the view. In Pointer mode, unless Pan is on, swipes
 * are shortcuts: up/down scroll the window under the cursor, right opens Type, left switches
 * to the next app. With Pan on, and in View and Scroll modes, they move the view.
 */
export function swipeAction(key: string, mode: ViewMode, pan: boolean): SwipeAction | null {
  const step = Object.hasOwn(ARROW_STEPS, key) ? ARROW_STEPS[key] : undefined;
  if (!step) return null;
  if (mode !== 'pointer' || pan) return { kind: 'pan', dx: step.dx, dy: step.dy };
  switch (key) {
    case 'ArrowUp':
      return { kind: 'scroll', dy: -SWIPE_SCROLL };
    case 'ArrowDown':
      return { kind: 'scroll', dy: SWIPE_SCROLL };
    case 'ArrowRight':
      return { kind: 'type' };
    default:
      return { kind: 'nextApp' };
  }
}

/** The app after `current` (1-based), wrapping round: 1 → 2 → … → count → 1. Null with no apps. */
export function nextAppSlot(current: number, count: number): number | null {
  return count > 0 ? (current % count) + 1 : null;
}

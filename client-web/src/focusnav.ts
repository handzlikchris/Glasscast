// Pressing buttons with a pinch on the glasses.
//
// On Meta Ray-Ban Display a swipe moves focus between buttons (arrow keys), but a
// pinch arrives as a pointer tap at the pointer's position, which is usually over
// the full-screen gesture layer rather than the focused button. So a tap on the
// gesture layer presses the focused button when focus was last moved by a swipe
// ("armed"). In Pointer mode a tap means "click in Windows", so there the focus is
// disarmed as soon as you drag or enter the mode, and pressing a button again takes
// a swipe first. The glasses may also send the same pinch as an Enter key; whichever
// arrives second is dropped so one pinch never presses twice.

import type { ViewMode } from './protocol';

/** A pointer tap and an Enter key this close together are the same pinch. */
export const SAME_PINCH_MS = 500;

export type TapRoute = 'pressFocused' | 'mode' | 'ignore';

export interface TapContext {
  /** Focus was last moved by a swipe (arrow key) and hasn't been disarmed since. */
  armed: boolean;
  /** A button or text box in the app currently holds the remembered focus. */
  hasFocused: boolean;
  /** Time since the last Enter key, in ms (Infinity if none). */
  msSinceEnter: number;
}

/** What a tap on the gesture layer should do. */
export function routeTap(c: TapContext): TapRoute {
  if (c.msSinceEnter < SAME_PINCH_MS) return 'ignore';
  if (c.armed && c.hasFocused) return 'pressFocused';
  return 'mode';
}

/** An Enter key right after pointer activity is the same pinch; the pointer path owns it. */
export function enterIsSamePinch(msSincePointer: number): boolean {
  return msSincePointer < SAME_PINCH_MS;
}

/** Whether focus stays armed after pressing a button that switches to `next` (or stays in the mode). */
export function armedAfterPress(next: ViewMode | null): boolean {
  return next !== 'pointer';
}

/** Dragging disarms the focus only in Pointer mode, where a tap means a Windows click. */
export function armedAfterDrag(mode: ViewMode, armed: boolean): boolean {
  return mode === 'pointer' ? false : armed;
}

export const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Tab']);

// Turns pointer events into taps and drags.
//
// On Meta Ray-Ban Display a Neural Band pinch-drag arrives as ordinary pointer
// events (pointerdown / pointermove / pointerup), as long as the page's initial
// CSS sets `touch-action: none`. A short pinch that stays within the tap
// threshold is a tap; anything that travels further is a drag. On a laptop the
// same code runs with mouse or trackpad drags.

export interface GestureOptions {
  /** Movement (view px) beyond which a press becomes a drag. */
  tapThreshold: number;
  /** Longest press still counted as a tap (ms). */
  tapMaxMs: number;
}

export const DEFAULT_GESTURES: GestureOptions = { tapThreshold: 10, tapMaxMs: 500 };

export type GestureEvent =
  | { kind: 'dragStart'; x: number; y: number }
  | { kind: 'drag'; dx: number; dy: number }
  | { kind: 'dragEnd' }
  | { kind: 'tap'; x: number; y: number };

interface Press {
  id: number;
  startX: number;
  startY: number;
  lastX: number;
  lastY: number;
  startTime: number;
  dragging: boolean;
}

/** Tracks one pointer at a time; extra pointers are ignored. */
export class GestureTracker {
  private press: Press | null = null;

  constructor(private readonly options: GestureOptions = DEFAULT_GESTURES) {}

  down(id: number, x: number, y: number, time: number): void {
    if (this.press) return;
    this.press = { id, startX: x, startY: y, lastX: x, lastY: y, startTime: time, dragging: false };
  }

  move(id: number, x: number, y: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];

    const events: GestureEvent[] = [];
    if (!p.dragging) {
      if (Math.hypot(x - p.startX, y - p.startY) <= this.options.tapThreshold) return [];
      p.dragging = true;
      events.push({ kind: 'dragStart', x: p.startX, y: p.startY });
    }

    // The first drag event includes the movement inside the threshold, so the
    // cursor doesn't lag behind the hand by the threshold distance.
    events.push({ kind: 'drag', dx: x - p.lastX, dy: y - p.lastY });
    p.lastX = x;
    p.lastY = y;
    return events;
  }

  up(id: number, x: number, y: number, time: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];
    this.press = null;

    if (p.dragging) {
      // Flush any last movement between the final pointermove and pointerup.
      const dx = x - p.lastX;
      const dy = y - p.lastY;
      const last: GestureEvent[] = dx !== 0 || dy !== 0 ? [{ kind: 'drag', dx, dy }] : [];
      return [...last, { kind: 'dragEnd' }];
    }
    if (time - p.startTime <= this.options.tapMaxMs) {
      return [{ kind: 'tap', x: p.startX, y: p.startY }];
    }
    return [];
  }

  cancel(id: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];
    this.press = null;
    return p.dragging ? [{ kind: 'dragEnd' }] : [];
  }
}

/** Longest gap between a tap and the next press for the two to count as one gesture (ms). */
export const DOUBLE_TAP_MS = 350;
/** How long the second press must last to count as "pinch, then pinch and hold" (ms). Movement doesn't matter. */
export const HOLD_MS = 500;

/**
 * Spots the start of "pinch, then pinch and hold": a press that begins within
 * DOUBLE_TAP_MS of the previous tap. The caller ignores that press's movement and waits
 * HOLD_MS: still down means "hold", released sooner means a quick second tap. Each tap can
 * start at most one such press.
 */
export class TapThenHold {
  private lastTapAt = -Infinity;

  tapped(time: number): void {
    this.lastTapAt = time;
  }

  /** Whether this press follows a tap closely enough to become a tap-then-hold. */
  pressStarted(time: number): boolean {
    const follows = time - this.lastTapAt <= DOUBLE_TAP_MS;
    this.lastTapAt = -Infinity;
    return follows;
  }
}

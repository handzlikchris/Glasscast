// Turns pointer events into taps and drags.
//
// On Meta Ray-Ban Display a Neural Band pinch-drag arrives as ordinary pointer
// events (pointerdown / pointermove / pointerup), as long as the page's initial
// CSS sets `touch-action: none`. A short pinch that stays within the tap
// threshold is a tap; anything that travels further is a drag. On a laptop the
// same code runs with mouse or trackpad drags.
//
// Hold (when the caller calls hold() HOLD_DRAG_MS after the press): a pinch kept still that
// long becomes a "hold". Moving after that is a held drag (the mouse button or the finger stays
// down: select text, drag a window, drag on the phone); releasing without moving ends the hold.
// Moving before it is a plain drag (the cursor moves, nothing is pressed).

export interface GestureOptions {
  /** Movement (view px) beyond which a press becomes a drag. */
  tapThreshold: number;
  /** Longest press still counted as a tap (ms). */
  tapMaxMs: number;
}

export const DEFAULT_GESTURES: GestureOptions = { tapThreshold: 10, tapMaxMs: 500 };

/** A pinch kept still this long becomes a hold: moving after it drags with the button/finger down. */
export const HOLD_DRAG_MS = 400;

export type GestureEvent =
  /** `held`: the press was held still first, so the drag keeps the button or finger down. */
  | { kind: 'dragStart'; x: number; y: number; held?: true }
  | { kind: 'drag'; dx: number; dy: number }
  | { kind: 'dragEnd'; held?: true }
  | { kind: 'tap'; x: number; y: number }
  /** Held still for HOLD_DRAG_MS: press the button / put the finger down at the start point. */
  | { kind: 'hold'; x: number; y: number }
  /** A hold released without moving. */
  | { kind: 'holdEnd' };

interface Press {
  id: number;
  startX: number;
  startY: number;
  lastX: number;
  lastY: number;
  startTime: number;
  dragging: boolean;
  held: boolean;
}

/** Tracks one pointer at a time; extra pointers are ignored. */
export class GestureTracker {
  private press: Press | null = null;

  constructor(private readonly options: GestureOptions = DEFAULT_GESTURES) {}

  down(id: number, x: number, y: number, time: number): void {
    if (this.press) return;
    this.press = { id, startX: x, startY: y, lastX: x, lastY: y, startTime: time, dragging: false, held: false };
  }

  move(id: number, x: number, y: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];

    const events: GestureEvent[] = [];
    if (!p.dragging) {
      if (Math.hypot(x - p.startX, y - p.startY) <= this.options.tapThreshold) return [];
      p.dragging = true;
      events.push(p.held ? { kind: 'dragStart', x: p.startX, y: p.startY, held: true } : { kind: 'dragStart', x: p.startX, y: p.startY });
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
      return [...last, dragEnd(p)];
    }
    if (p.held) return [{ kind: 'holdEnd' }];
    if (time - p.startTime <= this.options.tapMaxMs) {
      return [{ kind: 'tap', x: p.startX, y: p.startY }];
    }
    return [];
  }

  cancel(id: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];
    this.press = null;
    if (p.dragging) return [dragEnd(p)];
    return p.held ? [{ kind: 'holdEnd' }] : [];
  }

  /**
   * Call HOLD_DRAG_MS after down(): if the press is still down and hasn't moved, it becomes a hold
   * (and a later move a held drag). Does nothing otherwise.
   */
  hold(id: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id || p.dragging || p.held) return [];
    p.held = true;
    return [{ kind: 'hold', x: p.startX, y: p.startY }];
  }

  /** The press in progress is a hold or a held drag. */
  get holding(): boolean {
    return this.press?.held ?? false;
  }
}

function dragEnd(p: Press): GestureEvent {
  return p.held ? { kind: 'dragEnd', held: true } : { kind: 'dragEnd' };
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

// Turns pointer events into taps and drags.
//
// On Meta Ray-Ban Display a Neural Band pinch-drag arrives as ordinary pointer
// events (pointerdown / pointermove / pointerup), as long as the page's initial
// CSS sets `touch-action: none`. A tap is the default: a pinch let go within the
// grace time (tapGraceMs) is a tap however much the hand drifted, and its drift
// moves nothing. After the grace, travel beyond the tap threshold (measured from
// where the hand was when the grace ended) makes a drag. On a laptop the same code
// runs with mouse or trackpad drags.
//
// Tap-and-a-half, as on a laptop touchpad: the second pinch of a quick pair is "armed" (the
// caller passes armed to down()). An armed press that moves is a held drag (the mouse button or
// the finger stays down: select text, drag a window, drag on the phone); one kept still for
// HOLD_DRAG_MS (the caller calls hold()) holds the button where it is, and releasing ends the
// hold; one released quickly without moving is a tap (the second of a double tap). A press that
// isn't armed never holds: moving it only moves the cursor.

export interface GestureOptions {
  /** Movement (view px) beyond which a press becomes a drag. */
  tapThreshold: number;
  /** Longest press still counted as a tap (ms). */
  tapMaxMs: number;
  /**
   * The start of a press during which movement is ignored (ms): let go within it and it's a tap,
   * whatever the hand did. Drags start only after it (user's choice, 2026-09-29: quick pinches
   * were turning into drags). 0: none.
   */
  tapGraceMs?: number;
}

export const DEFAULT_GESTURES: GestureOptions = { tapThreshold: 14, tapMaxMs: 500, tapGraceMs: 200 };

/** An armed pinch (the second of a pair) kept still this long holds the button/finger down. */
export const HOLD_DRAG_MS = 400;

export type GestureEvent =
  /** `held`: an armed press (tap-and-a-half), so the drag keeps the button or finger down. */
  | { kind: 'dragStart'; x: number; y: number; held?: true }
  | { kind: 'drag'; dx: number; dy: number }
  | { kind: 'dragEnd'; held?: true }
  | { kind: 'tap'; x: number; y: number }
  /** An armed press held still for HOLD_DRAG_MS: press the button / put the finger down there. */
  | { kind: 'hold'; x: number; y: number }
  /** A hold released without moving. */
  | { kind: 'holdEnd' };

interface Press {
  id: number;
  startX: number;
  startY: number;
  /** Where travel is measured from: the start, or where the hand was when the grace ended. */
  anchorX: number;
  anchorY: number;
  lastX: number;
  lastY: number;
  startTime: number;
  dragging: boolean;
  held: boolean;
  /** The second pinch of a tap-and-a-half: its drag holds the button. */
  armed: boolean;
}

/** Tracks one pointer at a time; extra pointers are ignored. */
export class GestureTracker {
  private press: Press | null = null;

  constructor(private readonly options: GestureOptions = DEFAULT_GESTURES) {}

  down(id: number, x: number, y: number, time: number, armed = false): void {
    if (this.press) return;
    this.press = { id, startX: x, startY: y, anchorX: x, anchorY: y, lastX: x, lastY: y, startTime: time, dragging: false, held: false, armed };
  }

  /** `time`: the event's timestamp, for the grace; without it the grace is over. */
  move(id: number, x: number, y: number, time?: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id) return [];

    const events: GestureEvent[] = [];
    if (!p.dragging) {
      const grace = this.options.tapGraceMs ?? 0;
      if (grace > 0 && time !== undefined && time - p.startTime < grace) {
        // Still in the grace: a tap's drift. Nothing moves, and the drift doesn't count later.
        p.anchorX = p.lastX = x;
        p.anchorY = p.lastY = y;
        return [];
      }
      if (Math.hypot(x - p.anchorX, y - p.anchorY) <= this.options.tapThreshold) return [];
      p.dragging = true;
      events.push(p.held || p.armed ? { kind: 'dragStart', x: p.startX, y: p.startY, held: true } : { kind: 'dragStart', x: p.startX, y: p.startY });
    }

    // The first drag event includes the movement inside the threshold (since the grace ended),
    // so the cursor doesn't lag behind the hand by the threshold distance.
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
   * Call HOLD_DRAG_MS after an armed down(): if the press is still down and hasn't moved, it
   * becomes a hold. Does nothing otherwise (not armed, moved, released).
   */
  hold(id: number): GestureEvent[] {
    const p = this.press;
    if (!p || p.id !== id || !p.armed || p.dragging || p.held) return [];
    p.held = true;
    return [{ kind: 'hold', x: p.startX, y: p.startY }];
  }

  /** The press in progress is a hold or a held drag. */
  get holding(): boolean {
    return this.press?.held ?? false;
  }
}

function dragEnd(p: Press): GestureEvent {
  return p.held || p.armed ? { kind: 'dragEnd', held: true } : { kind: 'dragEnd' };
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

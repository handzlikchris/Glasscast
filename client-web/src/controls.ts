// Pointer and scroll behaviour, kept free of React and the DOM so it can be tested.
import type { Point, Rect } from './geometry';

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** Moves the software cursor by a drag delta, staying inside the content rect. */
export function moveCursor(cursor: Point, dx: number, dy: number, gain: number, bounds: Rect): Point {
  return {
    x: clamp(cursor.x + dx * gain, bounds.x, bounds.x + bounds.width - 1),
    y: clamp(cursor.y + dy * gain, bounds.y, bounds.y + bounds.height - 1),
  };
}

/** Cursor position as 0..1 within the content rect (what the server's "move" expects). */
export function toNormalized(cursor: Point, bounds: Rect): Point {
  return {
    x: clamp((cursor.x - bounds.x) / Math.max(1, bounds.width - 1), 0, 1),
    y: clamp((cursor.y - bounds.y) / Math.max(1, bounds.height - 1), 0, 1),
  };
}

export function centreOf(rect: Rect): Point {
  return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
}

/**
 * Collects vertical drag distance and releases it as wheel units.
 * Natural scrolling: dragging up moves the content up, i.e. scrolls down
 * (positive dy, like a browser's deltaY). 30 px of drag ≈ one wheel notch.
 */
export class ScrollAccumulator {
  private pending = 0;

  constructor(
    private readonly unitsPerPixel = 4,
    private readonly maxPerMessage = 1200,
  ) {}

  add(dragDy: number): void {
    this.pending += -dragDy * this.unitsPerPixel;
  }

  /** Whole wheel units ready to send (0 if nothing worth sending). */
  take(): number {
    const units = Math.trunc(this.pending);
    if (Math.abs(units) < 1) return 0;
    const sent = clamp(units, -this.maxPerMessage, this.maxPerMessage);
    this.pending -= sent;
    return sent;
  }
}

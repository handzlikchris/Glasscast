// Pointer and scroll behaviour, kept free of React and the DOM so it can be tested.
import { clampRegion, type Point, type Rect } from './geometry';
import type { Region, Size } from './protocol';

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/** Moves the software cursor by a drag delta, staying inside the content rect. */
export function moveCursor(cursor: Point, dx: number, dy: number, gain: number, bounds: Rect): Point {
  return {
    x: clamp(cursor.x + dx * gain, bounds.x, bounds.x + bounds.width - 1),
    y: clamp(cursor.y + dy * gain, bounds.y, bounds.y + bounds.height - 1),
  };
}

/** Which directions the region can still move on the monitor. */
export interface PanRoom {
  left: boolean;
  right: boolean;
  up: boolean;
  down: boolean;
}

export interface EdgePanResult {
  cursor: Point;
  /** How far to move the view, in view pixels (negative = left/up). */
  panX: number;
  panY: number;
}

/**
 * Pointer movement with edge panning. Inside the content the cursor moves as
 * usual. Once it is within `edgeZone` of an edge and the drag keeps pushing
 * outward, the cursor stops at the zone boundary and the rest of the push pans
 * the view instead, so you can slide the region across the monitor without
 * leaving Pointer mode. When the region is already at the monitor edge there
 * is no room to pan, and the cursor carries on to the real edge (so things at
 * the very edge of the screen stay clickable).
 */
export function moveCursorWithEdgePan(
  cursor: Point,
  dx: number,
  dy: number,
  gain: number,
  bounds: Rect,
  edgeZone: number,
  room: PanRoom,
): EdgePanResult {
  const [x, panX] = panAxis(cursor.x, dx * gain, bounds.x, bounds.x + bounds.width - 1, edgeZone, room.left, room.right);
  const [y, panY] = panAxis(cursor.y, dy * gain, bounds.y, bounds.y + bounds.height - 1, edgeZone, room.up, room.down);
  return { cursor: { x, y }, panX, panY };
}

function panAxis(
  pos: number,
  delta: number,
  lo: number,
  hi: number,
  zone: number,
  canPanNegative: boolean,
  canPanPositive: boolean,
): [number, number] {
  const target = pos + delta;

  if (delta < 0 && canPanNegative) {
    // Never pull a cursor that is already deeper in the zone back out of it.
    const stop = Math.min(pos, lo + zone);
    if (target < stop) return [stop, target - stop];
  }
  if (delta > 0 && canPanPositive) {
    const stop = Math.max(pos, hi - zone);
    if (target > stop) return [stop, target - stop];
  }
  return [clamp(target, lo, hi), 0];
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

/** Share of the view one arrow-button press moves the region by. Half keeps some overlap for context. */
export const NUDGE_FRACTION = 0.5;

/** The region moved one arrow-button step (dirX, dirY each -1, 0 or 1), kept on the monitor. */
export function nudgeRegion(region: Region, dirX: number, dirY: number, monitor: Size, fraction = NUDGE_FRACTION): Region {
  return clampRegion(
    {
      ...region,
      x: region.x + Math.round(dirX * region.width * fraction),
      y: region.y + Math.round(dirY * region.height * fraction),
    },
    monitor,
  );
}

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
/**
 * Pointer movement with the view locked (Pan off): the cursor goes right up to the edges, so
 * things at the very edge stay clickable, and the part of a push that would take it past the top
 * or bottom edge comes back as `overflowY` (view pixels, positive = down) for edge scrolling.
 */
export function moveCursorLocked(
  cursor: Point,
  dx: number,
  dy: number,
  gain: number,
  bounds: Rect,
): { cursor: Point; overflowY: number } {
  const top = bounds.y;
  const bottom = bounds.y + bounds.height - 1;
  const targetY = cursor.y + dy * gain;
  const overflowY = dy > 0 && targetY > bottom ? targetY - bottom : dy < 0 && targetY < top ? targetY - top : 0;
  return {
    cursor: { x: clamp(cursor.x + dx * gain, bounds.x, bounds.x + bounds.width - 1), y: clamp(targetY, top, bottom) },
    overflowY,
  };
}

/** Hold-to-scroll at the top (-1) or bottom (1) edge; `back` is how far the drag has come back in. */
export interface EdgeScroll {
  dir: 1 | -1;
  back: number;
}

/** How far (view px) a drag must come back in to stop edge scrolling; small wobbles don't. */
export const EDGE_SCROLL_RELEASE_PX = 12;

/**
 * One drag step of edge scrolling (Pan off). It starts when the cursor is pushed past the top or
 * bottom of the view, or when the glasses' own pointer is pushed against the top or bottom of the
 * display (after which it reports no more movement, so "keep pushing" is invisible to the page).
 * It keeps going while the drag stays put or pushes on, and stops once the drag comes back in.
 */
export function edgeScrollStep(
  state: EdgeScroll | null,
  step: { dy: number; overflowY: number; pointerPinned: -1 | 0 | 1 },
): EdgeScroll | null {
  if (state) {
    const inward = -step.dy * state.dir;
    const back = inward > 0 ? state.back + inward : 0;
    return back > EDGE_SCROLL_RELEASE_PX ? null : { dir: state.dir, back };
  }
  if (step.overflowY !== 0) return { dir: step.overflowY > 0 ? 1 : -1, back: 0 };
  if (step.pointerPinned !== 0 && step.dy * step.pointerPinned > 0) return { dir: step.pointerPinned, back: 0 };
  return null;
}

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

  /** Wheel units straight in (positive scrolls down), e.g. from edge scrolling. */
  addUnits(units: number): void {
    this.pending += units;
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

/** Share of the view one swipe moves the region by: a quarter keeps most of the view in place. */
export const NUDGE_FRACTION = 0.25;

/** The region moved one swipe step (dirX, dirY each -1, 0 or 1), kept on the monitor. */
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

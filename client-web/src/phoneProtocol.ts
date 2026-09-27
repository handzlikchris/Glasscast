// Glasses ⇄ phone, on the WebRTC DataChannel "input" (architecture/phone-mode.md). The PC never
// sees these: it only relays the signalling. Must stay in sync with the companion app's parser
// (android-companion/.../InputProtocol.kt).
//
// Positions are 0..1 within the video frame the glasses show; the phone maps them through its
// current crop to screen pixels.
import { fit, type Point, type Rect } from './geometry';
import { textChunks, type Region, type Size } from './protocol';

export type PhoneNav = 'back' | 'home' | 'recents' | 'notifications';
export type PhoneKey = 'Enter' | 'Backspace';

export type ToPhone =
  | { type: 'tap'; x: number; y: number }
  | { type: 'longPress'; x: number; y: number }
  | { type: 'swipe'; x1: number; y1: number; x2: number; y2: number; ms: number }
  | { type: 'nav'; action: PhoneNav }
  | { type: 'typeText'; text: string }
  | { type: 'key'; key: PhoneKey }
  /** Crop to this part of the screen (0..1), and stop following a window. */
  | { type: 'setRegion'; x: number; y: number; width: number; height: number }
  /** Crop to the top app window (e.g. a Samsung pop-up) and keep following it. */
  | { type: 'fitWindow' }
  | { type: 'ping'; t: number };

/** The crop, in 0..1 of the phone's screen. */
export type PhoneRegion = Region;

export type FromPhone =
  /** The phone's screen in pixels, the crop (0..1) and whether it follows a window. */
  | { type: 'screen'; width: number; height: number; region: PhoneRegion; follow: boolean }
  | { type: 'result'; of: 'typeText' | 'key'; ok: boolean }
  | { type: 'pong'; t: number };

export const SWIPE_MIN_MS = 50;
export const SWIPE_MAX_MS = 2000;

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const isNumber = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

/** Validates a message from the phone; anything unexpected is dropped. */
export function parsePhoneMessage(raw: string): FromPhone | null {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isObject(data)) return null;
  switch (data.type) {
    case 'screen': {
      const r = data.region;
      return isNumber(data.width) && isNumber(data.height) && isObject(r) &&
        isNumber(r.x) && isNumber(r.y) && isNumber(r.width) && isNumber(r.height)
        ? {
            type: 'screen',
            width: data.width,
            height: data.height,
            region: { x: r.x, y: r.y, width: r.width, height: r.height },
            follow: data.follow === true,
          }
        : null;
    }
    case 'result':
      return (data.of === 'typeText' || data.of === 'key') && typeof data.ok === 'boolean'
        ? { type: 'result', of: data.of, ok: data.ok }
        : null;
    case 'pong':
      return isNumber(data.t) ? { type: 'pong', t: data.t } : null;
    default:
      return null;
  }
}

/** Where the phone's frame sits in the 600×600 view (the video element letterboxes it). */
export function frameRect(video: Size, view: Size = { width: 600, height: 600 }): Rect {
  return fit(video, view);
}

/** A view point as 0..1 within the frame, clamped to it. */
export function toFrame(point: Point, frame: Rect): Point {
  return { x: clamp01((point.x - frame.x) / frame.width), y: clamp01((point.y - frame.y) / frame.height) };
}

export type SwipeDirection = 'up' | 'down' | 'left' | 'right';

/** How far a swipe travels, as a share of the frame, per strength step (see scrollSwipe). */
const SWIPE_SPAN = 0.35;
const SWIPE_MS = 220;

/**
 * The finger movement for a swipe on the glasses. "down" scrolls the content down (like a wheel
 * and the PC's swipe down), so the finger moves up; "left" pages to the next item, so the finger
 * moves left. Centred on `at` (0..1 in the frame) and kept inside the frame.
 */
export function scrollSwipe(direction: SwipeDirection, at: Point): ToPhone {
  const half = SWIPE_SPAN / 2;
  const centre = (v: number) => Math.min(1 - half - 0.05, Math.max(half + 0.05, v));
  const cx = centre(at.x);
  const cy = centre(at.y);
  const [x1, y1, x2, y2] = {
    down: [at.x, cy + half, at.x, cy - half],
    up: [at.x, cy - half, at.x, cy + half],
    left: [cx + half, at.y, cx - half, at.y],
    right: [cx - half, at.y, cx + half, at.y],
  }[direction];
  return { type: 'swipe', x1, y1, x2, y2, ms: SWIPE_MS };
}

const LINE_BREAKS = new Set([10, 13, 9, 0x2028, 0x2029]);

/** Text for the phone in typeText-sized pieces, with line breaks flattened (text never presses Enter). */
export function phoneText(text: string): string[] {
  // Line feed, carriage return, tab, line and paragraph separators (by code: no escapes to mangle).
  const flat = Array.from(text, (c) => (LINE_BREAKS.has(c.charCodeAt(0)) ? ' ' : c)).join('').trim();
  return flat ? textChunks(flat) : [];
}

// ---- choosing a region (Region mode) ----

export const FULL_REGION: PhoneRegion = { x: 0, y: 0, width: 1, height: 1 };
/** Smallest square, as a share of the screen's short side (the phone refuses under 0.1). */
export const MIN_SQUARE = 0.25;

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/**
 * A region that is square in the phone's pixels, `side` (share of the screen's short side)
 * wide, centred on `centre` (0..1) and kept on the screen.
 */
export function squareRegion(screen: Size, centre: { x: number; y: number }, side: number): PhoneRegion {
  const short = Math.min(screen.width, screen.height);
  const px = clamp(side, MIN_SQUARE, 1) * short;
  const width = px / screen.width;
  const height = px / screen.height;
  return {
    x: clamp(centre.x - width / 2, 0, 1 - width),
    y: clamp(centre.y - height / 2, 0, 1 - height),
    width,
    height,
  };
}

/** The square nearest a region, keeping its centre (for starting Region mode from any crop). */
export function squareAround(screen: Size, region: PhoneRegion): PhoneRegion {
  const short = Math.min(screen.width, screen.height);
  const side = Math.min(region.width * screen.width, region.height * screen.height) / short;
  return squareRegion(screen, centreOf(region), side);
}

/** Moves a region by (dx, dy) in 0..1 of the screen, kept on it. */
export function moveRegion(region: PhoneRegion, dx: number, dy: number): PhoneRegion {
  return {
    ...region,
    x: clamp(region.x + dx, 0, 1 - region.width),
    y: clamp(region.y + dy, 0, 1 - region.height),
  };
}

/** Grows (factor > 1) or shrinks a square region around its centre. */
export function zoomRegion(screen: Size, region: PhoneRegion, factor: number): PhoneRegion {
  const short = Math.min(screen.width, screen.height);
  const side = (region.width * screen.width * factor) / short;
  return squareRegion(screen, centreOf(region), side);
}

/** Where a region sits on the view, given where the whole screen's frame is. */
export function regionOnView(region: PhoneRegion, frame: Rect): Rect {
  return {
    x: frame.x + region.x * frame.width,
    y: frame.y + region.y * frame.height,
    width: region.width * frame.width,
    height: region.height * frame.height,
  };
}

function centreOf(region: PhoneRegion) {
  return { x: region.x + region.width / 2, y: region.y + region.height / 2 };
}

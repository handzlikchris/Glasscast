// Glasses ⇄ phone, on the WebRTC DataChannel "input" (architecture/phone-mode.md). The PC never
// sees these: it only relays the setup, and once the channel is open it isn't needed at all. Must stay in sync with the companion app's parser
// (android-companion/.../InputProtocol.kt).
//
// Positions are 0..1 within the video frame the glasses show; the phone maps them through its
// current crop to screen pixels.
import { fit, type Point, type Rect } from './geometry';
import { textChunks, type Region, type Size } from './protocol';

export type PhoneNav = 'back' | 'home' | 'recents' | 'notifications';
/** Send: the app's send button (else its editor action, else Enter). */
export type PhoneKey = 'Enter' | 'Backspace' | 'Send';

export type ToPhone =
  | { type: 'tap'; x: number; y: number }
  | { type: 'doubleTap'; x: number; y: number }
  /**
   * A finger held on the phone: down where a pinch was held still, moves with the drag, up on
   * release. Held without moving it is Android's own long press.
   */
  | { type: 'touch'; phase: 'down' | 'move' | 'up'; x: number; y: number }
  | { type: 'swipe'; x1: number; y1: number; x2: number; y2: number; ms: number }
  | { type: 'nav'; action: PhoneNav }
  | { type: 'typeText'; text: string }
  | { type: 'key'; key: PhoneKey }
  /**
   * Switch to the phone's previous (older) or next (newer) recently used app, like Alt+Tab: one
   * on screen (a split-screen half, a pop-up) is just selected, one that isn't is brought to the
   * front. The view then follows it. Never an app name: the phone keeps its own list.
   */
  | { type: 'switchApp'; dir: 'previous' | 'next' }
  /** Where the app overview's row of apps is (answered with overviewApps). */
  | { type: 'overviewApps' }
  /** Every couple of seconds: the phone ends a session it stops hearing from (no PC to tell it). */
  | { type: 'ping'; t: number }
  /** End on the glasses: the phone stops capturing. */
  | { type: 'end' };

/** The crop, in 0..1 of the phone's screen. */
export type PhoneRegion = Region;

export type FromPhone =
  /** The phone's screen in pixels, the crop (0..1): the window of the app in front, named if known. */
  | { type: 'screen'; width: number; height: number; region: PhoneRegion; app?: string }
  | { type: 'result'; of: 'typeText' | 'key' | 'switchApp'; ok: boolean }
  /** The app overview's row of apps, in order; empty when there's none (or the overview closed). */
  | { type: 'overviewApps'; apps: OverviewApp[] }
  | { type: 'pong'; t: number }
  /** The phone is ending the session, and why (just before it closes the connection). */
  | { type: 'bye'; reason: PhoneByeReason };

/** An app in the overview's row: its centre, 0..1 in the frame, and its name (for the status bar). */
export interface OverviewApp {
  x: number;
  y: number;
  label: string;
}

/** The most apps taken from the phone's overview row (the phone sends a handful). */
export const MAX_OVERVIEW_APPS = 12;

/** stopped: End session or Stop on the phone; capture: Android stopped the capture (the phone locked); replaced: another session took over; silent: nothing heard from the glasses. */
export type PhoneByeReason = 'stopped' | 'capture' | 'replaced' | 'silent';

const BYE_REASONS: readonly string[] = ['stopped', 'capture', 'replaced', 'silent'];

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
            // The followed app's name, for the status bar only; capped.
            ...(typeof data.app === 'string' && data.app.length > 0 ? { app: data.app.slice(0, 24) } : {}),
          }
        : null;
    }
    case 'result':
      return (data.of === 'typeText' || data.of === 'key' || data.of === 'switchApp') && typeof data.ok === 'boolean'
        ? { type: 'result', of: data.of, ok: data.ok }
        : null;
    case 'pong':
      return isNumber(data.t) ? { type: 'pong', t: data.t } : null;
    case 'overviewApps': {
      if (!Array.isArray(data.apps) || data.apps.length > MAX_OVERVIEW_APPS) return null;
      const apps: OverviewApp[] = [];
      for (const a of data.apps) {
        if (!isObject(a) || !isNumber(a.x) || !isNumber(a.y) || typeof a.label !== 'string') return null;
        apps.push({ x: clamp01(a.x), y: clamp01(a.y), label: a.label.slice(0, 24) });
      }
      return { type: 'overviewApps', apps };
    }
    case 'bye':
      return typeof data.reason === 'string' && BYE_REASONS.includes(data.reason)
        ? { type: 'bye', reason: data.reason as PhoneByeReason }
        : null;
    default:
      return null;
  }
}

/** The ended screen's text for a phone that said goodbye. */
export function describeBye(reason: PhoneByeReason): string {
  switch (reason) {
    case 'stopped':
      return 'The session was ended on the phone.';
    case 'capture':
      return 'The phone stopped sharing its screen (it locked, or its capture chip was tapped).';
    case 'replaced':
      return 'Another session with the phone took over.';
    case 'silent':
      return 'The phone stopped hearing from the glasses.';
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

/**
 * Moving through the app overview (Recents): a sideways swipe across the middle of the screen,
 * where the cards are, wherever the cursor is. "left" moves the cards left, so the finger moves
 * right (the other way round felt backwards on the glasses, 2026-09-30).
 * Shorter and slower than a page swipe: half the width in 220 ms was a fling that skipped two
 * apps (seen on the S25 at 1080×1080, density 320). The companion also stops the finger before
 * lifting while the overview is open (no fling), so the distance alone moves it: 0.4 of the width,
 * past half a card and short of one and a half.
 */
export function appsSwipe(direction: 'left' | 'right'): ToPhone {
  const [x1, x2] = direction === 'left' ? [0.3, 0.7] : [0.7, 0.3];
  return { type: 'swipe', x1, y1: 0.5, x2, y2: 0.5, ms: APPS_SWIPE_MS };
}

const APPS_SWIPE_MS = 350;

const LINE_BREAKS = new Set([10, 13, 9, 0x2028, 0x2029]);

/** Text for the phone in typeText-sized pieces, with line breaks flattened (text never presses Enter). */
export function phoneText(text: string): string[] {
  // Line feed, carriage return, tab, line and paragraph separators (by code: no escapes to mangle).
  const flat = Array.from(text, (c) => (LINE_BREAKS.has(c.charCodeAt(0)) ? ' ' : c)).join('').trim();
  return flat ? textChunks(flat) : [];
}

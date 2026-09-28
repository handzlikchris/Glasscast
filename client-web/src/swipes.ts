// Swipe shortcuts on the view: the one place that says what a swipe does, for PC and phone
// sessions alike (the interface decision is in architecture/glasses-client.md, "Swipes").
//
// A thumb swipe on the Neural Band arrives as an arrow key. On the view:
//
//   swipe right twice      Type, on the PC and the phone
//   swipe left twice       PC: next app shortcut · phone: Back
//   swipe up twice         phone: the previous app (PC: up/down don't wait for a double)
//   swipe down twice       phone: the next app
//   a single swipe         the target's plain action (scroll, pan, page) after DOUBLE_SWIPE_MS
//                          without a second one; on the PC up/down act at once
//
// A swipe that waits for a double does its plain action that much later. An up or down swipe
// while a left/right waits drops the waiting one: the band reads some down-swipes as left, and
// that stray left must not act.

import type { ViewMode } from './protocol';

export type Swipe = 'up' | 'down' | 'left' | 'right';
export type SwipeGesture = Swipe | 'doubleLeft' | 'doubleRight' | 'doubleUp' | 'doubleDown';

const DOUBLES: Record<Swipe, SwipeGesture> = {
  left: 'doubleLeft',
  right: 'doubleRight',
  up: 'doubleUp',
  down: 'doubleDown',
};

const vertical = (s: Swipe) => s === 'up' || s === 'down';

/** How long a left or right swipe waits for a second one in the same direction (ms). */
export const DOUBLE_SWIPE_MS = 300;

const SWIPE_KEYS: Readonly<Record<string, Swipe>> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
};

/** The swipe an arrow key stands for, or null for any other key. */
export function swipeOf(key: string): Swipe | null {
  return Object.hasOwn(SWIPE_KEYS, key) ? SWIPE_KEYS[key] : null;
}

export interface SwipeTimers {
  set(fn: () => void, ms: number): unknown;
  clear(handle: unknown): void;
}

const browserTimers: SwipeTimers = {
  set: (fn, ms) => setTimeout(fn, ms),
  clear: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

/**
 * Turns swipes into gestures: up and down at once, left and right after DOUBLE_SWIPE_MS unless
 * a second one in the same direction makes it a double. `onWaiting` hears when a left or right
 * starts waiting (to show "again for Type" and the like).
 */
export class SwipeReader {
  private pending: { swipe: Swipe; handle: unknown } | null = null;
  private readonly waitFor: ReadonlySet<Swipe>;

  /**
   * `waitFor`: the swipes that wait for a second one (a PC session: left and right; a phone
   * session: all four). The others act at once.
   */
  constructor(
    private readonly onGesture: (gesture: SwipeGesture) => void,
    private readonly onWaiting: (swipe: Swipe) => void = () => {},
    private readonly timers: SwipeTimers = browserTimers,
    waitFor: readonly Swipe[] = ['left', 'right'],
  ) {
    this.waitFor = new Set(waitFor);
  }

  swipe(swipe: Swipe): void {
    if (this.pending?.swipe === swipe) {
      this.cancel();
      this.onGesture(DOUBLES[swipe]);
      return;
    }
    const previous = this.pending?.swipe;
    this.cancel();
    // A left or right that an up/down follows was the band misreading a vertical swipe: dropped.
    // Otherwise the one waiting was a single swipe after all.
    if (previous && !(vertical(swipe) && !vertical(previous))) this.onGesture(previous);
    if (!this.waitFor.has(swipe)) {
      this.onGesture(swipe);
      return;
    }
    const handle = this.timers.set(() => {
      if (this.pending?.handle !== handle) return;
      this.pending = null;
      this.onGesture(swipe);
    }, DOUBLE_SWIPE_MS);
    this.pending = { swipe, handle };
    this.onWaiting(swipe);
  }

  /** Drops a swipe that is still waiting (the view lost the swipes, or the session ended). */
  cancel(): void {
    if (this.pending) this.timers.clear(this.pending.handle);
    this.pending = null;
  }
}

// ---- what each gesture does ----

export type PcSwipeAction =
  | { kind: 'pan'; dx: number; dy: number }
  /** Direction only; the strength comes from the ↕ setting (scrollPrefs.ts). -1 up, 1 down. */
  | { kind: 'scroll'; dir: -1 | 1 }
  | { kind: 'type' }
  | { kind: 'nextApp' }
  | { kind: 'none' };

const STEPS: Record<Swipe, { dx: number; dy: number }> = {
  up: { dx: 0, dy: -1 },
  down: { dx: 0, dy: 1 },
  left: { dx: -1, dy: 0 },
  right: { dx: 1, dy: 0 },
};

/**
 * A PC session. The doubles are the same in every view mode. Single swipes: in Pointer mode
 * (Pan off) up/down scroll the window under the cursor and left/right do nothing; with Pan on,
 * and in View and Scroll modes, they move the view.
 */
export function pcSwipeAction(gesture: SwipeGesture, mode: ViewMode, pan: boolean): PcSwipeAction {
  if (gesture === 'doubleRight') return { kind: 'type' };
  if (gesture === 'doubleLeft') return { kind: 'nextApp' };
  // A PC session's up/down don't wait for a double, so these never come.
  if (gesture === 'doubleUp' || gesture === 'doubleDown') return { kind: 'none' };
  if (mode !== 'pointer' || pan) return { kind: 'pan', ...STEPS[gesture] };
  if (gesture === 'up') return { kind: 'scroll', dir: -1 };
  if (gesture === 'down') return { kind: 'scroll', dir: 1 };
  return { kind: 'none' };
}

export type PhoneSwipeAction =
  | { kind: 'swipe'; direction: Swipe }
  | { kind: 'type' }
  | { kind: 'back' }
  /** The phone's previous (older) or next (newer) recently used app, followed by Fit. */
  | { kind: 'app'; dir: 'previous' | 'next' };

/** The swipes that wait for a double in a phone session: all four. */
export const PHONE_DOUBLES: readonly Swipe[] = ['up', 'down', 'left', 'right'];

/**
 * A phone session: single swipes become the same finger swipe on the phone around the cursor
 * (up/down scroll, left/right page); right twice opens Type, left twice presses the phone's Back,
 * up twice goes to the previous app and down twice to the next one.
 */
export function phoneSwipeAction(gesture: SwipeGesture): PhoneSwipeAction {
  switch (gesture) {
    case 'doubleRight':
      return { kind: 'type' };
    case 'doubleLeft':
      return { kind: 'back' };
    case 'doubleUp':
      return { kind: 'app', dir: 'previous' };
    case 'doubleDown':
      return { kind: 'app', dir: 'next' };
    default:
      return { kind: 'swipe', direction: gesture };
  }
}

/** What the status bar says while a left or right waits for its second swipe. */
export function waitingHint(swipe: Swipe, target: 'pc' | 'phone'): string {
  switch (swipe) {
    case 'right':
      return 'swipe right again for Type';
    case 'left':
      return target === 'pc' ? 'swipe left again for the next app' : 'swipe left again for Back';
    case 'up':
      return 'swipe up again for the previous app';
    case 'down':
      return 'swipe down again for the next app';
  }
}

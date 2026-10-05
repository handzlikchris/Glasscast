import { describe, expect, it } from 'vitest';
import type { AppProfile } from './apps/profile';
import { WALK } from './apps/walk';
import {
  DOUBLE_SWIPE_MS,
  PHONE_DOUBLES,
  SwipeReader,
  pcSwipeAction,
  phoneDoubles,
  phoneSwipeAction,
  swipeOf,
  waitingHint,
  type SwipeGesture,
  type SwipeTimers,
} from './swipes';

/** Timers driven by the test: run(ms) fires everything due by then. */
function fakeTimers() {
  let now = 0;
  let next = 0;
  const due = new Map<number, { at: number; fn: () => void }>();
  const timers: SwipeTimers = {
    set: (fn, ms) => {
      due.set(++next, { at: now + ms, fn });
      return next;
    },
    clear: (handle) => due.delete(handle as number),
  };
  const run = (ms: number) => {
    now += ms;
    for (const [id, t] of [...due].sort((a, b) => a[1].at - b[1].at)) {
      if (t.at <= now) {
        due.delete(id);
        t.fn();
      }
    }
  };
  return { timers, run };
}

function reader() {
  const { timers, run } = fakeTimers();
  const gestures: SwipeGesture[] = [];
  const waiting: string[] = [];
  const r = new SwipeReader((g) => gestures.push(g), (s) => waiting.push(s), timers);
  return { r, run, gestures, waiting };
}

describe('SwipeReader', () => {
  it('up and down act at once', () => {
    const { r, gestures } = reader();
    r.swipe('down');
    r.swipe('up');
    expect(gestures).toEqual(['down', 'up']);
  });

  it('two rights within the wait are a double; one alone acts after it', () => {
    const { r, run, gestures, waiting } = reader();
    r.swipe('right');
    run(DOUBLE_SWIPE_MS - 100);
    r.swipe('right');
    expect(gestures).toEqual(['doubleRight']);
    expect(waiting).toEqual(['right']);

    r.swipe('left');
    run(DOUBLE_SWIPE_MS - 1);
    expect(gestures).toEqual(['doubleRight']);
    run(1);
    expect(gestures).toEqual(['doubleRight', 'left']);
  });

  it('a second swipe after the wait starts a new one', () => {
    const { r, run, gestures } = reader();
    r.swipe('left');
    run(DOUBLE_SWIPE_MS + 50);
    r.swipe('left');
    run(DOUBLE_SWIPE_MS);
    expect(gestures).toEqual(['left', 'left']);
  });

  it('the other direction settles the waiting swipe as a single', () => {
    const { r, run, gestures } = reader();
    r.swipe('left');
    r.swipe('right');
    expect(gestures).toEqual(['left']);
    run(DOUBLE_SWIPE_MS);
    expect(gestures).toEqual(['left', 'right']);
  });

  it('up or down drops a waiting left (the band reads some down-swipes as left)', () => {
    const { r, run, gestures } = reader();
    r.swipe('left');
    r.swipe('down');
    run(DOUBLE_SWIPE_MS * 2);
    expect(gestures).toEqual(['down']);
  });

  it('cancel drops a waiting swipe', () => {
    const { r, run, gestures } = reader();
    r.swipe('right');
    r.cancel();
    run(DOUBLE_SWIPE_MS * 2);
    expect(gestures).toEqual([]);
  });
});

describe('swipeOf', () => {
  it('maps arrow keys only', () => {
    expect(swipeOf('ArrowLeft')).toBe('left');
    expect(swipeOf('Enter')).toBeNull();
    expect(swipeOf('toString')).toBeNull();
  });
});

describe('pcSwipeAction', () => {
  it('doubles are Type and next app in every mode', () => {
    for (const [mode, pan] of [['pointer', false], ['pointer', true], ['view', false], ['scroll', false]] as const) {
      expect(pcSwipeAction('doubleRight', mode, pan)).toEqual({ kind: 'type' });
      expect(pcSwipeAction('doubleLeft', mode, pan)).toEqual({ kind: 'nextApp' });
    }
  });

  it('in Pointer mode up/down scroll and single left/right do nothing', () => {
    expect(pcSwipeAction('up', 'pointer', false)).toEqual({ kind: 'scroll', dir: -1 });
    expect(pcSwipeAction('down', 'pointer', false)).toEqual({ kind: 'scroll', dir: 1 });
    expect(pcSwipeAction('left', 'pointer', false)).toEqual({ kind: 'none' });
    expect(pcSwipeAction('right', 'pointer', false)).toEqual({ kind: 'none' });
  });

  it('with Pan on, and in View and Scroll modes, single swipes pan', () => {
    expect(pcSwipeAction('left', 'pointer', true)).toEqual({ kind: 'pan', dx: -1, dy: 0 });
    expect(pcSwipeAction('up', 'view', false)).toEqual({ kind: 'pan', dx: 0, dy: -1 });
    expect(pcSwipeAction('right', 'scroll', false)).toEqual({ kind: 'pan', dx: 1, dy: 0 });
  });
});

describe('phoneSwipeAction', () => {
  it('single swipes swipe the phone; right twice is Type, left twice the app overview, down twice Back', () => {
    expect(phoneSwipeAction('down')).toEqual({ kind: 'swipe', direction: 'down' });
    expect(phoneSwipeAction('left')).toEqual({ kind: 'swipe', direction: 'left' });
    expect(phoneSwipeAction('doubleLeft')).toEqual({ kind: 'apps' });
    expect(phoneSwipeAction('doubleDown')).toEqual({ kind: 'back' });
    expect(phoneSwipeAction('doubleRight')).toEqual({ kind: 'type' });
  });
});

describe('SwipeReader in a phone session (down, left and right wait for a double)', () => {
  function phoneReader() {
    const { timers, run } = fakeTimers();
    const gestures: SwipeGesture[] = [];
    const r = new SwipeReader((g) => gestures.push(g), () => {}, timers, PHONE_DOUBLES);
    return { r, run, gestures };
  }

  it('up scrolls at once, even twice; down and left twice are doubles, one alone acts after the wait', () => {
    const { r, run, gestures } = phoneReader();
    r.swipe('up');
    r.swipe('up');
    expect(gestures).toEqual(['up', 'up']);
    r.swipe('down');
    run(DOUBLE_SWIPE_MS - 50);
    r.swipe('down');
    r.swipe('left');
    r.swipe('left');
    expect(gestures).toEqual(['up', 'up', 'doubleDown', 'doubleLeft']);
    r.swipe('down');
    run(DOUBLE_SWIPE_MS);
    expect(gestures).toEqual(['up', 'up', 'doubleDown', 'doubleLeft', 'down']);
  });

  it('a waiting left that a down follows is dropped (the band reads some downs as left)', () => {
    const { r, run, gestures } = phoneReader();
    r.swipe('left');
    r.swipe('down');
    run(DOUBLE_SWIPE_MS);
    expect(gestures).toEqual(['down']);
  });
});

describe('a phone app profile', () => {
  const headings: AppProfile = {
    ...WALK,
    name: 'Browser',
    gestures: { ...WALK.gestures, doubleRight: { walk: 'next', unit: 'heading' } },
  };

  it('comes first for the gestures it takes, the rest stay generic', () => {
    expect(phoneSwipeAction('doubleDown', WALK)).toEqual({ kind: 'profile', action: { walk: 'next', unit: 'item' } });
    expect(phoneSwipeAction('doubleUp', WALK)).toEqual({ kind: 'profile', action: { walk: 'previous', unit: 'item' } });
    expect(phoneSwipeAction('doubleRight', WALK)).toEqual({ kind: 'type' });
    expect(phoneSwipeAction('doubleLeft', WALK)).toEqual({ kind: 'apps' });
    expect(phoneSwipeAction('down', WALK)).toEqual({ kind: 'swipe', direction: 'down' });
    expect(phoneSwipeAction('doubleRight', headings)).toEqual({ kind: 'profile', action: { walk: 'next', unit: 'heading' } });
    expect(phoneSwipeAction('doubleDown', null)).toEqual({ kind: 'back' });
  });

  it('makes a swipe wait for a double only where it maps one (up, with Walk)', () => {
    expect(phoneDoubles(null).sort()).toEqual([...PHONE_DOUBLES].sort());
    expect(phoneDoubles(WALK).sort()).toEqual(['down', 'left', 'right', 'up']);
  });

  it("the reader takes the profile's waits when the app changes", () => {
    const { timers, run } = fakeTimers();
    const gestures: SwipeGesture[] = [];
    const r = new SwipeReader((g) => gestures.push(g), () => {}, timers, PHONE_DOUBLES);
    r.swipe('up');
    expect(gestures).toEqual(['up']);
    r.setWaitFor(phoneDoubles(WALK));
    r.swipe('up');
    expect(gestures).toEqual(['up']);
    r.swipe('up');
    expect(gestures).toEqual(['up', 'doubleUp']);
    r.swipe('up');
    run(DOUBLE_SWIPE_MS);
    expect(gestures).toEqual(['up', 'doubleUp', 'up']);
  });

  it('says what the second swipe will do', () => {
    expect(waitingHint('down', 'phone', WALK)).toBe('swipe down again for the next item');
    expect(waitingHint('right', 'phone', headings)).toBe('swipe right again for the next heading');
    expect(waitingHint('right', 'phone', WALK)).toBe('swipe right again for Type');
  });
});

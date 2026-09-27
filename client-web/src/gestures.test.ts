import { describe, expect, it } from 'vitest';
import { DOUBLE_TAP_MS, GestureTracker, TapThenHold } from './gestures';

describe('GestureTracker', () => {
  it('reports a short press within the threshold as a tap', () => {
    const g = new GestureTracker({ tapThreshold: 10, tapMaxMs: 500 });
    g.down(1, 100, 100, 0);
    expect(g.move(1, 104, 103)).toEqual([]);
    expect(g.up(1, 104, 103, 120)).toEqual([{ kind: 'tap', x: 100, y: 100 }]);
  });

  it('turns movement beyond the threshold into a drag, including the initial travel', () => {
    const g = new GestureTracker({ tapThreshold: 10, tapMaxMs: 500 });
    g.down(1, 100, 100, 0);
    expect(g.move(1, 112, 100)).toEqual([
      { kind: 'dragStart', x: 100, y: 100 },
      { kind: 'drag', dx: 12, dy: 0 },
    ]);
    expect(g.move(1, 115, 96)).toEqual([{ kind: 'drag', dx: 3, dy: -4 }]);
    expect(g.up(1, 115, 96, 300)).toEqual([{ kind: 'dragEnd' }]);
  });

  it('flushes movement between the last move and release', () => {
    const g = new GestureTracker({ tapThreshold: 10, tapMaxMs: 500 });
    g.down(1, 0, 0, 0);
    g.move(1, 20, 0);
    expect(g.up(1, 25, 2, 100)).toEqual([{ kind: 'drag', dx: 5, dy: 2 }, { kind: 'dragEnd' }]);
  });

  it('does not click after a drag that returns to the start', () => {
    const g = new GestureTracker({ tapThreshold: 10, tapMaxMs: 500 });
    g.down(1, 100, 100, 0);
    g.move(1, 130, 100);
    g.move(1, 100, 100);
    const events = g.up(1, 100, 100, 200);
    expect(events.some((e) => e.kind === 'tap')).toBe(false);
  });

  it('ignores long presses and second pointers', () => {
    const g = new GestureTracker({ tapThreshold: 10, tapMaxMs: 500 });
    g.down(1, 0, 0, 0);
    g.down(2, 50, 50, 10);
    expect(g.move(2, 200, 200)).toEqual([]);
    expect(g.up(1, 0, 0, 900)).toEqual([]);
  });

  it('ends a drag on cancel', () => {
    const g = new GestureTracker();
    g.down(1, 0, 0, 0);
    g.move(1, 50, 0);
    expect(g.cancel(1)).toEqual([{ kind: 'dragEnd' }]);
  });
});

describe('TapThenHold', () => {
  it('arms a press that starts soon after a tap', () => {
    const t = new TapThenHold();
    t.tapped(1000);
    expect(t.pressStarted(1000 + DOUBLE_TAP_MS)).toBe(true);
  });

  it('ignores a press that starts too late or with no tap before it', () => {
    const t = new TapThenHold();
    expect(t.pressStarted(0)).toBe(false);
    t.tapped(1000);
    expect(t.pressStarted(1001 + DOUBLE_TAP_MS)).toBe(false);
  });

  it('lets one tap arm only one press', () => {
    const t = new TapThenHold();
    t.tapped(1000);
    expect(t.pressStarted(1100)).toBe(true);
    expect(t.pressStarted(1200)).toBe(false);
  });
});

describe('GestureTracker holds', () => {
  const options = { tapThreshold: 10, tapMaxMs: 500 };

  it('a press kept still becomes a hold, and moving after it is a held drag', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0);
    expect(g.move(1, 104, 102)).toEqual([]);
    expect(g.hold(1)).toEqual([{ kind: 'hold', x: 100, y: 100 }]);
    expect(g.holding).toBe(true);
    expect(g.move(1, 120, 100)).toEqual([
      { kind: 'dragStart', x: 100, y: 100, held: true },
      { kind: 'drag', dx: 20, dy: 0 },
    ]);
    expect(g.up(1, 120, 100, 900)).toEqual([{ kind: 'dragEnd', held: true }]);
    expect(g.holding).toBe(false);
  });

  it('a hold released without moving ends the hold, not a tap', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0);
    g.hold(1);
    expect(g.up(1, 3, 0, 450)).toEqual([{ kind: 'holdEnd' }]);
  });

  it('moving before the hold is a plain drag, and the late hold does nothing', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0);
    expect(g.move(1, 30, 0)[0]).toEqual({ kind: 'dragStart', x: 0, y: 0 });
    expect(g.hold(1)).toEqual([]);
    expect(g.up(1, 30, 0, 600)).toEqual([{ kind: 'dragEnd' }]);
  });

  it('cancelling a hold or a held drag ends it', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0);
    g.hold(1);
    expect(g.cancel(1)).toEqual([{ kind: 'holdEnd' }]);
    g.down(2, 0, 0, 0);
    g.hold(2);
    g.move(2, 50, 0);
    expect(g.cancel(2)).toEqual([{ kind: 'dragEnd', held: true }]);
  });

  it('a hold for another pointer or after release does nothing', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0);
    expect(g.hold(2)).toEqual([]);
    g.up(1, 0, 0, 100);
    expect(g.hold(1)).toEqual([]);
  });
});

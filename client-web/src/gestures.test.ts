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

describe('GestureTracker grace (a tap is the default)', () => {
  const options = { tapThreshold: 14, tapMaxMs: 500, tapGraceMs: 200 };

  it('a pinch let go within the grace is a tap, however far the hand drifted', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0);
    expect(g.move(1, 160, 130, 150)).toEqual([]);
    expect(g.up(1, 170, 140, 190)).toEqual([{ kind: 'tap', x: 100, y: 100 }]);
  });

  it('after the grace, travel from where the hand then was makes a drag, without the drift', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0);
    g.move(1, 150, 100, 100); // drift in the grace
    expect(g.move(1, 160, 100, 250)).toEqual([]); // 10 px since: still under the threshold
    expect(g.move(1, 170, 100, 300)).toEqual([
      { kind: 'dragStart', x: 100, y: 100 },
      { kind: 'drag', dx: 20, dy: 0 },
    ]);
    expect(g.up(1, 170, 100, 400)).toEqual([{ kind: 'dragEnd' }]);
  });

  it('a slow pinch that stayed put is still a tap', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0);
    g.move(1, 105, 104, 300);
    expect(g.up(1, 105, 104, 450)).toEqual([{ kind: 'tap', x: 100, y: 100 }]);
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

describe('GestureTracker tap-and-a-half (armed presses)', () => {
  const options = { tapThreshold: 10, tapMaxMs: 500 };

  it('an armed press that moves is a held drag from the start', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0, true);
    expect(g.move(1, 120, 100)).toEqual([
      { kind: 'dragStart', x: 100, y: 100, held: true },
      { kind: 'drag', dx: 20, dy: 0 },
    ]);
    expect(g.up(1, 120, 100, 300)).toEqual([{ kind: 'dragEnd', held: true }]);
  });

  it('an armed press released quickly without moving is a tap (the second of a double tap)', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0, true);
    expect(g.up(1, 2, 0, 120)).toEqual([{ kind: 'tap', x: 0, y: 0 }]);
  });

  it('an armed press kept still holds, then drags or is let go', () => {
    const g = new GestureTracker(options);
    g.down(1, 100, 100, 0, true);
    expect(g.hold(1)).toEqual([{ kind: 'hold', x: 100, y: 100 }]);
    expect(g.holding).toBe(true);
    expect(g.move(1, 100, 130)[0]).toEqual({ kind: 'dragStart', x: 100, y: 100, held: true });
    expect(g.up(1, 100, 130, 900)).toEqual([{ kind: 'dragEnd', held: true }]);

    g.down(2, 0, 0, 0, true);
    g.hold(2);
    expect(g.up(2, 0, 0, 700)).toEqual([{ kind: 'holdEnd' }]);
  });

  it('a press that is not armed never holds: it only moves the cursor', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0);
    expect(g.hold(1)).toEqual([]);
    expect(g.move(1, 30, 0)[0]).toEqual({ kind: 'dragStart', x: 0, y: 0 });
    expect(g.up(1, 30, 0, 600)).toEqual([{ kind: 'dragEnd' }]);
  });

  it('cancelling ends a hold or a held drag', () => {
    const g = new GestureTracker(options);
    g.down(1, 0, 0, 0, true);
    g.hold(1);
    expect(g.cancel(1)).toEqual([{ kind: 'holdEnd' }]);
    g.down(2, 0, 0, 0, true);
    g.move(2, 50, 0);
    expect(g.cancel(2)).toEqual([{ kind: 'dragEnd', held: true }]);
  });
});

import { describe, expect, it } from 'vitest';
import { GestureTracker } from './gestures';

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

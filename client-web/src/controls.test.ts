import { describe, expect, it } from 'vitest';
import { centreOf, moveCursor, ScrollAccumulator, toNormalized } from './controls';

const bounds = { x: 0, y: 131, width: 600, height: 338 };

describe('cursor', () => {
  it('moves by drag delta times gain', () => {
    expect(moveCursor({ x: 300, y: 300 }, 10, -5, 1.5, bounds)).toEqual({ x: 315, y: 292.5 });
  });

  it('stays inside the content rect', () => {
    expect(moveCursor({ x: 590, y: 140 }, 100, -100, 1, bounds)).toEqual({ x: 599, y: 131 });
  });

  it('normalises to 0..1 within the content', () => {
    expect(toNormalized({ x: 0, y: 131 }, bounds)).toEqual({ x: 0, y: 0 });
    expect(toNormalized({ x: 599, y: 468 }, bounds)).toEqual({ x: 1, y: 1 });
    expect(toNormalized(centreOf(bounds), bounds).x).toBeCloseTo(0.5, 2);
  });
});

describe('ScrollAccumulator', () => {
  it('turns upward drags into positive (scroll down) wheel units', () => {
    const acc = new ScrollAccumulator(4);
    acc.add(-30);
    expect(acc.take()).toBe(120);
    expect(acc.take()).toBe(0);
  });

  it('keeps fractional remainders for the next message', () => {
    const acc = new ScrollAccumulator(0.5);
    acc.add(-1);
    expect(acc.take()).toBe(0);
    acc.add(-1);
    expect(acc.take()).toBe(1);
  });

  it('caps each message', () => {
    const acc = new ScrollAccumulator(4, 1200);
    acc.add(1000);
    expect(acc.take()).toBe(-1200);
    expect(acc.take()).toBe(-1200);
    expect(acc.take()).toBe(-1200);
    expect(acc.take()).toBe(-400);
  });
});

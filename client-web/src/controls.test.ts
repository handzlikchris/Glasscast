import { describe, expect, it } from 'vitest';
import { centreOf, moveCursor, moveCursorWithEdgePan, ScrollAccumulator, toNormalized } from './controls';

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

describe('moveCursorWithEdgePan', () => {
  const view = { x: 0, y: 0, width: 600, height: 600 };
  const everywhere = { left: true, right: true, up: true, down: true };
  const nowhere = { left: false, right: false, up: false, down: false };
  const ZONE = 24;

  it('moves normally away from the edges', () => {
    expect(moveCursorWithEdgePan({ x: 300, y: 300 }, 10, -10, 1, view, ZONE, everywhere)).toEqual({
      cursor: { x: 310, y: 290 },
      panX: 0,
      panY: 0,
    });
  });

  it('stops at the edge zone and turns the rest of the push into a pan', () => {
    // 20 px short of the zone, pushed 50 px: 20 moves the cursor, 30 pans.
    const r = moveCursorWithEdgePan({ x: 555, y: 300 }, 50, 0, 1, view, ZONE, everywhere);
    expect(r.cursor.x).toBe(599 - ZONE);
    expect(r.panX).toBe(30);
    expect(r.panY).toBe(0);
  });

  it('keeps panning while the cursor sits in the zone and the push continues', () => {
    const r = moveCursorWithEdgePan({ x: 300, y: ZONE }, 0, -15, 1, view, ZONE, everywhere);
    expect(r.cursor.y).toBe(ZONE);
    expect(r.panY).toBe(-15);
  });

  it('moves the cursor out of the zone without panning when dragging back inward', () => {
    expect(moveCursorWithEdgePan({ x: 590, y: 300 }, -40, 0, 1, view, ZONE, everywhere)).toEqual({
      cursor: { x: 550, y: 300 },
      panX: 0,
      panY: 0,
    });
  });

  it('lets the cursor reach the real edge when the region cannot pan further', () => {
    expect(moveCursorWithEdgePan({ x: 590, y: 5 }, 50, -50, 1, view, ZONE, nowhere)).toEqual({
      cursor: { x: 599, y: 0 },
      panX: 0,
      panY: 0,
    });
  });

  it('pans each axis independently', () => {
    // Already inside the right zone: the whole 20 px push pans; the vertical drag just moves.
    const r = moveCursorWithEdgePan({ x: 590, y: 300 }, 20, 10, 1, view, ZONE, { ...nowhere, right: true });
    expect(r.cursor.x).toBe(590);
    expect(r.panX).toBe(20);
    expect(r.cursor.y).toBe(310);
    expect(r.panY).toBe(0);
  });

  it('never pulls a cursor deeper in the zone back to the zone boundary', () => {
    // Cursor already at x=595 (inside the zone, e.g. after the region hit the monitor edge
    // and later gained room): a further push pans from where it is.
    const r = moveCursorWithEdgePan({ x: 595, y: 300 }, 10, 0, 1, view, ZONE, everywhere);
    expect(r.cursor.x).toBe(595);
    expect(r.panX).toBe(10);
  });
});

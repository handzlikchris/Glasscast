import { describe, expect, it } from 'vitest';
import { ASPECTS, clampRegion, contentRect, fit, moveRegion, regionInOverview, resizeRegion } from './geometry';

const monitor = { width: 2560, height: 1440 };

describe('fit', () => {
  it('letterboxes a 16:9 monitor like the server does', () => {
    // Must match RegionMath.Fit in the server tests.
    expect(fit(monitor)).toEqual({ x: 0, y: 131, width: 600, height: 338 });
  });

  it('fills the view for square sources', () => {
    expect(fit({ width: 800, height: 800 })).toEqual({ x: 0, y: 0, width: 600, height: 600 });
  });
});

describe('clampRegion', () => {
  it('keeps the region on the monitor', () => {
    expect(clampRegion({ x: 2400, y: 1300, width: 600, height: 600 }, monitor)).toEqual({
      x: 1960,
      y: 840,
      width: 600,
      height: 600,
    });
  });

  it('enforces the minimum size', () => {
    const r = clampRegion({ x: 0, y: 0, width: 10, height: 10 }, monitor);
    expect(r.width).toBe(160);
    expect(r.height).toBe(160);
  });
});

describe('overview mapping', () => {
  it('places a region inside the letterboxed monitor', () => {
    const r = regionInOverview({ x: 0, y: 0, width: 2560, height: 1440 }, monitor);
    expect(r.x).toBe(0);
    expect(r.y).toBe(131);
    expect(r.width).toBeCloseTo(600);
  });

  it('moves the region by overview pixels scaled up to monitor pixels', () => {
    const moved = moveRegion({ x: 100, y: 100, width: 600, height: 600 }, 60, 0, monitor);
    expect(moved.x).toBe(100 + 256);
    expect(moved.y).toBe(100);
  });

  it('never drags a region off the monitor', () => {
    const moved = moveRegion({ x: 100, y: 100, width: 600, height: 600 }, -500, 5000, monitor);
    expect(moved).toEqual({ x: 0, y: 840, width: 600, height: 600 });
  });
});

describe('resizeRegion', () => {
  it('scales about the centre and applies the aspect ratio', () => {
    const r = resizeRegion({ x: 1000, y: 400, width: 600, height: 600 }, 1, ASPECTS.wide, monitor);
    expect(r.height).toBe(600);
    expect(r.width).toBe(1067);
    // Centre stays put, give or take the half pixel lost to rounding.
    expect(Math.abs(r.x + r.width / 2 - 1300)).toBeLessThanOrEqual(1);
  });

  it('shrinks to fit the monitor without breaking the aspect', () => {
    const r = resizeRegion({ x: 0, y: 0, width: 1400, height: 1400 }, 2, ASPECTS.square, monitor);
    expect(r.width).toBe(1440);
    expect(r.height).toBe(1440);
  });

  it('respects the minimum size', () => {
    const r = resizeRegion({ x: 0, y: 0, width: 200, height: 200 }, 0.1, ASPECTS.square, monitor);
    expect(r.height).toBe(160);
  });
});

describe('contentRect', () => {
  it('letterboxes a wide region inside the view', () => {
    expect(contentRect({ x: 0, y: 0, width: 1600, height: 900 })).toEqual({ x: 0, y: 131, width: 600, height: 338 });
  });
});

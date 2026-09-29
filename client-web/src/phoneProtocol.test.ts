import { describe, expect, it } from 'vitest';
import {
  frameRect,
  moveRegion,
  parsePhoneMessage,
  phoneText,
  regionOnView,
  scrollSwipe,
  squareAround,
  squareRegion,
  toFrame,
  zoomRegion,
} from './phoneProtocol';

describe('parsePhoneMessage', () => {
  it('accepts the three phone messages', () => {
    expect(parsePhoneMessage('{"type":"screen","width":1080,"height":2340,"region":{"x":0,"y":0.1,"width":1,"height":0.5}}')).toEqual({
      type: 'screen',
      width: 1080,
      height: 2340,
      region: { x: 0, y: 0.1, width: 1, height: 0.5 },
      follow: false,
    });
    expect(parsePhoneMessage('{"type":"screen","width":1,"height":1,"region":{"x":0,"y":0,"width":1,"height":1},"follow":true}')).toMatchObject({ follow: true });
    expect(parsePhoneMessage('{"type":"screen","width":1,"height":1,"region":{"x":0,"y":0,"width":1,"height":1},"follow":true,"app":"Chrome"}')).toMatchObject({ app: 'Chrome' });
    expect(parsePhoneMessage('{"type":"result","of":"typeText","ok":false}')).toEqual({ type: 'result', of: 'typeText', ok: false });
    expect(parsePhoneMessage('{"type":"pong","t":5}')).toEqual({ type: 'pong', t: 5 });
    expect(parsePhoneMessage('{"type":"bye","reason":"capture"}')).toEqual({ type: 'bye', reason: 'capture' });
  });

  it.each([
    'nope',
    '{"type":"screen","width":1080}',
    '{"type":"result","of":"tap","ok":true}',
    '{"type":"tap","x":1,"y":1}',
    '{"type":"bye","reason":"because"}',
    'null',
  ])('drops %s', (raw) => {
    expect(parsePhoneMessage(raw)).toBeNull();
  });
});

describe('frame mapping', () => {
  it('letterboxes a portrait phone frame and maps view points into it', () => {
    const frame = frameRect({ width: 277, height: 600 });
    expect(frame).toEqual({ x: 161, y: 0, width: 277, height: 600 });
    expect(toFrame({ x: 161 + 277 / 2, y: 300 }, frame)).toEqual({ x: 0.5, y: 0.5 });
    // Outside the frame clamps to its edge.
    expect(toFrame({ x: 10, y: 700 }, frame)).toEqual({ x: 0, y: 1 });
  });
});

describe('scrollSwipe', () => {
  it('scrolling down drags the finger up, around the cursor', () => {
    const swipe = scrollSwipe('down', { x: 0.3, y: 0.5 });
    expect(swipe).toMatchObject({ type: 'swipe', x1: 0.3, x2: 0.3 });
    if (swipe.type !== 'swipe') throw new Error();
    expect(swipe.y1).toBeGreaterThan(swipe.y2);
  });

  it('stays inside the frame near its edges', () => {
    const swipe = scrollSwipe('up', { x: 0.5, y: 0.98 });
    if (swipe.type !== 'swipe') throw new Error();
    for (const v of [swipe.y1, swipe.y2]) {
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThanOrEqual(1);
    }
  });

  it('paging left drags the finger left', () => {
    const swipe = scrollSwipe('left', { x: 0.5, y: 0.5 });
    if (swipe.type !== 'swipe') throw new Error();
    expect(swipe.x1).toBeGreaterThan(swipe.x2);
  });
});

describe('phoneText', () => {
  it('flattens line breaks and splits long text', () => {
    expect(phoneText(' hello\nworld ')).toEqual(['hello world']);
    const pieces = phoneText('a'.repeat(1200));
    expect(pieces.map((p) => p.length)).toEqual([500, 500, 200]);
    expect(phoneText('  \n ')).toEqual([]);
  });
});

describe('choosing a region', () => {
  const s25 = { width: 1080, height: 2340 };
  const px = (r: { width: number; height: number }) => [Math.round(r.width * s25.width), Math.round(r.height * s25.height)];

  it('makes squares in phone pixels, kept on the screen', () => {
    const top = squareRegion(s25, { x: 0.5, y: 0 }, 1);
    expect(px(top)).toEqual([1080, 1080]);
    expect(top.y).toBe(0);
    const bottom = squareRegion(s25, { x: 0.9, y: 1 }, 0.5);
    expect(px(bottom)).toEqual([540, 540]);
    expect(bottom.x + bottom.width).toBeCloseTo(1);
    expect(bottom.y + bottom.height).toBeCloseTo(1);
  });

  it('zooms around the centre within limits', () => {
    const start = squareRegion(s25, { x: 0.5, y: 0.5 }, 0.8);
    const smaller = zoomRegion(s25, start, 0.5);
    expect(px(smaller)).toEqual([432, 432]);
    expect(smaller.x + smaller.width / 2).toBeCloseTo(0.5);
    expect(px(zoomRegion(s25, start, 10))).toEqual([1080, 1080]);
    expect(px(zoomRegion(s25, start, 0.01))).toEqual([270, 270]);
  });

  it('moves and stays on the screen', () => {
    const r = squareRegion(s25, { x: 0.5, y: 0.5 }, 0.5);
    expect(moveRegion(r, 1, 1)).toMatchObject({ x: 1 - r.width, y: 1 - r.height });
    expect(moveRegion(r, -1, -1)).toMatchObject({ x: 0, y: 0 });
  });

  it('squares off any crop around its centre', () => {
    const window = { x: 0.1, y: 0.2, width: 0.8, height: 0.3 };
    const square = squareAround(s25, window);
    expect(px(square)[0]).toBe(px(square)[1]);
    expect(square.y + square.height / 2).toBeCloseTo(0.35);
  });

  it('places a region on the letterboxed full frame', () => {
    expect(regionOnView({ x: 0.5, y: 0.25, width: 0.5, height: 0.25 }, { x: 161, y: 0, width: 277, height: 600 })).toEqual({
      x: 299.5,
      y: 150,
      width: 138.5,
      height: 150,
    });
  });
});

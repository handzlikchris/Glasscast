import { describe, expect, it } from 'vitest';
import {
  frameRect,
  parsePhoneMessage,
  phoneText,
  scrollSwipe,
  appsSwipe,
  toFrame,
} from './phoneProtocol';

describe('parsePhoneMessage', () => {
  it('accepts the three phone messages', () => {
    expect(parsePhoneMessage('{"type":"screen","width":1080,"height":2340,"region":{"x":0,"y":0.1,"width":1,"height":0.5}}')).toEqual({
      type: 'screen',
      width: 1080,
      height: 2340,
      region: { x: 0, y: 0.1, width: 1, height: 0.5 },
    });
    expect(parsePhoneMessage('{"type":"screen","width":1,"height":1,"region":{"x":0,"y":0,"width":1,"height":1},"app":"Chrome"}')).toMatchObject({ app: 'Chrome' });
    expect(parsePhoneMessage('{"type":"result","of":"typeText","ok":false}')).toEqual({ type: 'result', of: 'typeText', ok: false });
    expect(parsePhoneMessage('{"type":"pong","t":5}')).toEqual({ type: 'pong', t: 5 });
    expect(parsePhoneMessage('{"type":"bye","reason":"capture"}')).toEqual({ type: 'bye', reason: 'capture' });
    expect(parsePhoneMessage('{"type":"overviewApps","apps":[{"x":0.15,"y":0.85,"label":"WhatsApp"},{"x":1.2,"y":0.85,"label":"Gmail"}]}')).toEqual({
      type: 'overviewApps',
      apps: [
        { x: 0.15, y: 0.85, label: 'WhatsApp' },
        { x: 1, y: 0.85, label: 'Gmail' },
      ],
    });
    expect(parsePhoneMessage('{"type":"overviewApps","apps":[]}')).toEqual({ type: 'overviewApps', apps: [] });
    expect(parsePhoneMessage('{"type":"overviewApps","apps":[{"x":0.5,"y":0.5}]}')).toBeNull();
    expect(parsePhoneMessage(JSON.stringify({ type: 'overviewApps', apps: Array(13).fill({ x: 0, y: 0, label: '' }) }))).toBeNull();
  });

  it("reads the followed app's package, and drops a malformed one", () => {
    const screen = (pkg: unknown) =>
      parsePhoneMessage(JSON.stringify({ type: 'screen', width: 1, height: 1, region: { x: 0, y: 0, width: 1, height: 1 }, app: 'Chrome', pkg }));
    expect(screen('com.android.chrome')).toMatchObject({ app: 'Chrome', pkg: 'com.android.chrome' });
    expect(screen('../etc')).not.toHaveProperty('pkg');
    expect(screen('a'.repeat(101))).not.toHaveProperty('pkg');
  });

  it('accepts controls and walked, clamping boxes and capping labels', () => {
    const send = { x: 0.75, y: 0.875, w: 0.5, h: 0.2, kind: 'button', label: 'Send', id: '' };
    expect(parsePhoneMessage(JSON.stringify({ type: 'controls', pkg: 'com.anthropic.claude', items: [send] }))).toEqual({
      type: 'controls',
      pkg: 'com.anthropic.claude',
      items: [{ x: 0.75, y: 0.875, w: 0.25, h: 0.125, kind: 'button', label: 'Send', id: '' }],
    });
    expect(parsePhoneMessage('{"type":"controls","pkg":"","items":[]}')).toEqual({ type: 'controls', pkg: '', items: [] });
    expect(parsePhoneMessage(JSON.stringify({ type: 'walked', pkg: 'com.android.chrome', item: { ...send, kind: 'link', label: 'x'.repeat(99) } })))
      .toMatchObject({ item: { kind: 'link', label: 'x'.repeat(40) } });
    expect(parsePhoneMessage('{"type":"walked","pkg":"com.android.chrome"}')).toEqual({ type: 'walked', pkg: 'com.android.chrome' });
    // Too many, a bad kind or a missing field: the whole message goes.
    expect(parsePhoneMessage(JSON.stringify({ type: 'controls', pkg: '', items: Array(65).fill(send) }))).toBeNull();
    expect(parsePhoneMessage(JSON.stringify({ type: 'controls', pkg: '', items: [{ ...send, kind: 'script' }] }))).toBeNull();
    expect(parsePhoneMessage(JSON.stringify({ type: 'walked', pkg: 'x y', item: send }))).toBeNull();
    expect(parsePhoneMessage(JSON.stringify({ type: 'walked', pkg: '', item: { ...send, label: undefined } }))).toBeNull();
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

describe('appsSwipe', () => {
  it('swipes across the middle of the screen, whatever the cursor; left moves the cards left', () => {
    expect(appsSwipe('left')).toMatchObject({ type: 'swipe', x1: 0.3, x2: 0.7, y1: 0.5, y2: 0.5, ms: 350 });
    expect(appsSwipe('right')).toMatchObject({ x1: 0.7, x2: 0.3 });
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

  it('a gentler level makes a shorter scroll, not a shorter page', () => {
    const length = (s: ReturnType<typeof scrollSwipe>) =>
      s.type === 'swipe' ? Math.hypot(s.x2 - s.x1, s.y2 - s.y1) : NaN;
    const full = length(scrollSwipe('down', { x: 0.5, y: 0.5 }));
    expect(length(scrollSwipe('down', { x: 0.5, y: 0.5 }, 0.75))).toBeCloseTo(full * 0.75);
    expect(length(scrollSwipe('left', { x: 0.5, y: 0.5 }, 0.25))).toBeCloseTo(full);
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

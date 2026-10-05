import { describe, expect, it } from 'vitest';
import { shortcutRows } from '../shortcuts';
import { phoneDoubles, phoneSwipeAction } from '../swipes';
import { CHROME } from './chrome';
import { activeProfile } from './index';

describe('the Chrome profile', () => {
  it("is Chrome's (and its beta's) own, on by default", () => {
    expect(activeProfile('com.android.chrome', {}).profile).toBe(CHROME);
    expect(activeProfile('com.chrome.beta', {}).profile).toBe(CHROME);
  });

  it('hops by item with down/up twice and by heading with right/left twice', () => {
    expect(phoneSwipeAction('doubleDown', CHROME)).toEqual({ kind: 'profile', action: { walk: 'next', unit: 'item' } });
    expect(phoneSwipeAction('doubleUp', CHROME)).toEqual({ kind: 'profile', action: { walk: 'previous', unit: 'item' } });
    expect(phoneSwipeAction('doubleRight', CHROME)).toEqual({ kind: 'profile', action: { walk: 'next', unit: 'heading' } });
    expect(phoneSwipeAction('doubleLeft', CHROME)).toEqual({ kind: 'profile', action: { walk: 'previous', unit: 'heading' } });
    expect(CHROME.whileHighlighted).toBe('view');
  });

  it('still scrolls with single up and down (after the wait, as every swipe has a double)', () => {
    expect(phoneSwipeAction('up', CHROME)).toEqual({ kind: 'swipe', direction: 'up' });
    expect(phoneSwipeAction('down', CHROME)).toEqual({ kind: 'swipe', direction: 'down' });
    expect(phoneDoubles(CHROME).sort()).toEqual(['down', 'left', 'right', 'up']);
  });

  it('shows its hops in the ? panel, and none of the generic doubles it took', () => {
    const rows = shortcutRows('phone', { profile: CHROME });
    expect(rows[0]).toMatchObject({ gesture: 'In Chrome', heading: true });
    const action = (gesture: string) => rows.find((r) => r.gesture === gesture)?.action;
    expect(action('swipe down ×2')).toBe('next item');
    expect(action('swipe up ×2')).toBe('previous item');
    expect(action('swipe right ×2')).toBe('next heading');
    expect(action('swipe left ×2')).toBe('previous heading');
    expect(rows.filter((r) => r.gesture.endsWith('×2'))).toHaveLength(4);
    expect(action('app overview')).toBe('Back, then pinch (Apps)');
  });
});

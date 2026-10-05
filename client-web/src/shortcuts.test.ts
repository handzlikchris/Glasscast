import { describe, expect, it } from 'vitest';
import { WALK } from './apps/walk';
import { shortcutRows } from './shortcuts';

const actionFor = (target: 'pc' | 'phone', gesture: string) =>
  shortcutRows(target).find((row) => row.gesture === gesture)?.action;

describe('the ? panel', () => {
  it("lists the phone's swipe shortcuts as swipes.ts maps them", () => {
    expect(actionFor('phone', 'swipe right ×2')).toBe('Type');
    expect(actionFor('phone', 'swipe left ×2')).toBe('app overview');
    expect(actionFor('phone', 'swipe down ×2')).toBe('Back');
    expect(actionFor('phone', 'swipe up ×2')).toBeUndefined();
    expect(actionFor('phone', 'pinch')).toBe('tap');
    expect(actionFor('phone', 'in the app overview')).toContain('pinch picks');
  });

  it("lists the PC's", () => {
    expect(actionFor('pc', 'swipe right ×2')).toBe('Type');
    expect(actionFor('pc', 'swipe left ×2')).toBe('next app');
    expect(actionFor('pc', 'swipe down ×2')).toBeUndefined();
    expect(actionFor('pc', 'pinch')).toBe('click');
    expect(actionFor('pc', 'in the app overview')).toBeUndefined();
  });

  it("puts a phone app's profile first, under its name, and leaves out the generic doubles it took", () => {
    const rows = shortcutRows('phone', { profile: WALK });
    expect(rows[0]).toEqual({ gesture: 'In Walk', action: '', heading: true });
    const generic = rows.slice(rows.findIndex((r) => r.gesture === 'Everywhere'));
    const ownRows = rows.slice(0, rows.findIndex((r) => r.gesture === 'Everywhere'));
    expect(ownRows.find((r) => r.gesture === 'swipe down ×2')?.action).toBe('next item');
    expect(ownRows.find((r) => r.gesture === 'swipe up ×2')?.action).toBe('previous item');
    // Walk took down twice: Back is gone from the generic rows; Type and the app overview stay.
    expect(generic.find((r) => r.gesture === 'swipe down ×2')).toBeUndefined();
    expect(generic.find((r) => r.gesture === 'swipe right ×2')?.action).toBe('Type');
    expect(generic.find((r) => r.gesture === 'swipe left ×2')?.action).toBe('app overview');
  });

  it("says when the app's own profile is off", () => {
    const rows = shortcutRows('phone', { profile: null, offName: 'Chrome' });
    expect(rows[0]).toEqual({ gesture: 'Chrome profile off (✦ turns it on)', action: '', heading: true });
    expect(rows.find((r) => r.gesture === 'swipe down ×2')?.action).toBe('Back');
  });
});

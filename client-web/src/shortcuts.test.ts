import { describe, expect, it } from 'vitest';
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
});

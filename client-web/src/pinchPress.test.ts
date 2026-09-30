import { describe, expect, it } from 'vitest';
import { SAME_PINCH_MS } from './focusnav';
import { OnePressPerPinch } from './pinchPress';

describe('one press per pinch', () => {
  it("takes a pinch's tap or its Enter, whichever comes first, not both", () => {
    const presses = new OnePressPerPinch();
    expect(presses.press(1000)).toBe(true);
    expect(presses.press(1000 + 120)).toBe(false);
  });

  it('takes the next pinch', () => {
    const presses = new OnePressPerPinch();
    expect(presses.press(1000)).toBe(true);
    expect(presses.press(1000 + SAME_PINCH_MS)).toBe(true);
  });

  it('measures from the press it took, not the one it dropped', () => {
    const presses = new OnePressPerPinch();
    presses.press(1000);
    presses.press(1000 + 400);
    expect(presses.press(1000 + SAME_PINCH_MS + 10)).toBe(true);
  });
});

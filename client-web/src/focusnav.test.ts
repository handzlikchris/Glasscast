import { describe, expect, it } from 'vitest';
import { armedAfterDrag, armedAfterPress, enterIsSamePinch, routeTap, SAME_PINCH_MS } from './focusnav';

describe('routeTap', () => {
  it('presses the focused button after a swipe', () => {
    expect(routeTap({ armed: true, hasFocused: true, msSinceEnter: Infinity })).toBe('pressFocused');
  });

  it('falls through to the mode when focus was not moved by a swipe', () => {
    expect(routeTap({ armed: false, hasFocused: true, msSinceEnter: Infinity })).toBe('mode');
  });

  it('falls through to the mode when nothing is focused', () => {
    expect(routeTap({ armed: true, hasFocused: false, msSinceEnter: Infinity })).toBe('mode');
  });

  it('ignores a tap that follows an Enter from the same pinch', () => {
    expect(routeTap({ armed: true, hasFocused: true, msSinceEnter: 100 })).toBe('ignore');
    expect(routeTap({ armed: false, hasFocused: false, msSinceEnter: 100 })).toBe('ignore');
  });

  it('treats an Enter long before the tap as a separate press', () => {
    expect(routeTap({ armed: true, hasFocused: true, msSinceEnter: SAME_PINCH_MS })).toBe('pressFocused');
  });
});

describe('enterIsSamePinch', () => {
  it('drops an Enter right after pointer activity', () => {
    expect(enterIsSamePinch(50)).toBe(true);
  });

  it('keeps an Enter with no pointer activity nearby', () => {
    expect(enterIsSamePinch(SAME_PINCH_MS)).toBe(false);
    expect(enterIsSamePinch(Infinity)).toBe(false);
  });
});

describe('arming', () => {
  it('disarms when a press enters Pointer mode, so the next pinch clicks in Windows', () => {
    expect(armedAfterPress('pointer')).toBe(false);
    expect(armedAfterPress('overview')).toBe(true);
    expect(armedAfterPress(null)).toBe(true);
  });

  it('disarms on drag only in Pointer mode', () => {
    expect(armedAfterDrag('pointer', true)).toBe(false);
    expect(armedAfterDrag('overview', true)).toBe(true);
    expect(armedAfterDrag('scroll', false)).toBe(false);
  });
});

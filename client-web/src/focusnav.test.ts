import { describe, expect, it } from 'vitest';
import { enterIsSamePinch, menuFocusFor, navAfterMode, routeTap, SAME_PINCH_MS } from './focusnav';

describe('routeTap', () => {
  it('presses the focused button while on the controls', () => {
    expect(routeTap({ nav: 'controls', hasFocused: true, msSinceEnter: Infinity })).toBe('pressFocused');
  });

  it('leaves the tap to the mode while on the view', () => {
    expect(routeTap({ nav: 'view', hasFocused: true, msSinceEnter: Infinity })).toBe('mode');
  });

  it('leaves the tap to the mode when nothing is focused', () => {
    expect(routeTap({ nav: 'controls', hasFocused: false, msSinceEnter: Infinity })).toBe('mode');
  });

  it('ignores a tap that follows an Enter from the same pinch', () => {
    expect(routeTap({ nav: 'controls', hasFocused: true, msSinceEnter: 100 })).toBe('ignore');
    expect(routeTap({ nav: 'view', hasFocused: false, msSinceEnter: 100 })).toBe('ignore');
  });

  it('treats an Enter long before the tap as a separate press', () => {
    expect(routeTap({ nav: 'controls', hasFocused: true, msSinceEnter: SAME_PINCH_MS })).toBe('pressFocused');
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

describe('navAfterMode', () => {
  it('sends swipes to the view in View, Pointer and Scroll', () => {
    expect(navAfterMode('view')).toBe('view');
    expect(navAfterMode('pointer')).toBe('view');
    expect(navAfterMode('scroll')).toBe('view');
  });

  it('keeps swipes on the controls in Overview and Type, which have their own buttons', () => {
    expect(navAfterMode('overview')).toBe('controls');
    expect(navAfterMode('type')).toBe('controls');
  });
});

describe('menuFocusFor', () => {
  it('focuses the likely next step: Type from Pointer, Pointer from View or Scroll', () => {
    expect(menuFocusFor('pointer')).toBe('type');
    expect(menuFocusFor('view')).toBe('pointer');
    expect(menuFocusFor('scroll')).toBe('pointer');
  });

  it('stays on the current mode in Overview and Type', () => {
    expect(menuFocusFor('overview')).toBe('overview');
    expect(menuFocusFor('type')).toBe('type');
  });
});

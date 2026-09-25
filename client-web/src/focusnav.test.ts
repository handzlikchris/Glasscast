import { describe, expect, it } from 'vitest';
import {
  enterIsSamePinch,
  menuFocusFor,
  navAfterBack,
  navAfterMode,
  nextAppSlot,
  routeTap,
  SAME_PINCH_MS,
  swipeAction,
  SWIPE_SCROLL,
} from './focusnav';

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

describe('navAfterBack', () => {
  it('toggles between the view and the controls in View, Pointer and Scroll', () => {
    expect(navAfterBack('view', 'pointer')).toBe('controls');
    expect(navAfterBack('controls', 'pointer')).toBe('view');
    expect(navAfterBack('controls', 'scroll')).toBe('view');
  });

  it('stays on the controls in Overview and Type', () => {
    expect(navAfterBack('controls', 'type')).toBe('controls');
    expect(navAfterBack('controls', 'overview')).toBe('controls');
  });
});

describe('swipeAction', () => {
  it('makes swipes shortcuts in Pointer mode: scroll, Type, next app', () => {
    expect(swipeAction('ArrowUp', 'pointer', false)).toEqual({ kind: 'scroll', dy: -SWIPE_SCROLL });
    expect(swipeAction('ArrowDown', 'pointer', false)).toEqual({ kind: 'scroll', dy: SWIPE_SCROLL });
    expect(swipeAction('ArrowRight', 'pointer', false)).toEqual({ kind: 'type' });
    expect(swipeAction('ArrowLeft', 'pointer', false)).toEqual({ kind: 'nextApp' });
  });

  it('pans with Pan on, and always in View and Scroll modes', () => {
    expect(swipeAction('ArrowLeft', 'pointer', true)).toEqual({ kind: 'pan', dx: -1, dy: 0 });
    expect(swipeAction('ArrowUp', 'view', false)).toEqual({ kind: 'pan', dx: 0, dy: -1 });
    expect(swipeAction('ArrowRight', 'scroll', false)).toEqual({ kind: 'pan', dx: 1, dy: 0 });
  });

  it('ignores keys that are not swipes', () => {
    expect(swipeAction('Enter', 'pointer', false)).toBeNull();
    expect(swipeAction('toString', 'pointer', false)).toBeNull();
  });
});

describe('nextAppSlot', () => {
  it('cycles 1 → 2 → … → count → 1', () => {
    expect(nextAppSlot(1, 3)).toBe(2);
    expect(nextAppSlot(3, 3)).toBe(1);
    expect(nextAppSlot(1, 1)).toBe(1);
  });

  it('has nothing to switch to without apps', () => {
    expect(nextAppSlot(1, 0)).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import {
  enterIsSamePinch,
  menuFocusFor,
  backTarget,
  navAfterMode,
  nextAppSlot,
  wrapAround,
  routeTap,
  SAME_PINCH_MS,
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

describe('backTarget', () => {
  it('goes from the view up to the controls', () => {
    expect(backTarget('view')).toBe('controls');
  });

  it('goes from the controls (Type, Overview, the mode bar) home to Pointer', () => {
    expect(backTarget('controls')).toBe('pointer');
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

describe('wrapAround', () => {
  const row = ['a', 'b', 'c'];
  it('wraps from the ends of a row to the other end', () => {
    expect(wrapAround(row, 'a', 'ArrowLeft')).toBe('c');
    expect(wrapAround(row, 'c', 'ArrowRight')).toBe('a');
  });
  it('leaves the rest to the glasses', () => {
    expect(wrapAround(row, 'b', 'ArrowLeft')).toBeNull();
    expect(wrapAround(row, 'a', 'ArrowRight')).toBeNull();
    expect(wrapAround(row, 'a', 'ArrowUp')).toBeNull();
    expect(wrapAround(row, 'x', 'ArrowLeft')).toBeNull();
    expect(wrapAround(['a'], 'a', 'ArrowLeft')).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import { edgeUnitsPerSecond, levelFor, nextScrollLevel, parseScrollLevels, swipeUnits } from './scrollPrefs';

describe('scroll levels', () => {
  it('step down and wrap round', () => {
    expect(nextScrollLevel(9)).toBe(5);
    expect(nextScrollLevel(1)).toBe(9);
  });

  it('are per app, nine notches until set', () => {
    const levels = { Browser: 3 as const };
    expect(levelFor(levels, 'Browser')).toBe(3);
    expect(levelFor(levels, 'Claude')).toBe(9);
    expect(swipeUnits(3)).toBe(360);
    expect(edgeUnitsPerSecond(3)).toBe(180);
  });

  it('keep only well-formed stored entries', () => {
    expect(parseScrollLevels('{"Browser":3,"Claude":7,"x":"9"}')).toEqual({ Browser: 3 });
    expect(parseScrollLevels('not json')).toEqual({});
    expect(parseScrollLevels('[1,2]')).toEqual({});
    expect(parseScrollLevels(null)).toEqual({});
  });
});

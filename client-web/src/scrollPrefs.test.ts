import { describe, expect, it } from 'vitest';
import {
  edgeUnitsPerSecond,
  levelFor,
  nextPhoneScrollLevel,
  nextScrollLevel,
  parsePhoneScrollLevels,
  parseScrollLevels,
  phoneLevelFor,
  swipeUnits,
} from './scrollPrefs';

describe('phone scroll levels', () => {
  it('start at three quarters, step down and wrap round', () => {
    expect(phoneLevelFor({}, 'Claude')).toBe(75);
    expect(nextPhoneScrollLevel(75)).toBe(50);
    expect(nextPhoneScrollLevel(25)).toBe(150);
  });

  it('keep only well-formed stored levels', () => {
    expect(parsePhoneScrollLevels('{"Claude":50,"Chrome":3,"x":"75"}')).toEqual({ Claude: 50 });
    expect(parsePhoneScrollLevels('nope')).toEqual({});
    expect(parsePhoneScrollLevels(null)).toEqual({});
  });
});

describe('scroll levels', () => {
  it('step down and wrap round', () => {
    expect(nextScrollLevel(9)).toBe(5);
    expect(nextScrollLevel(1)).toBe(9);
  });

  it('are per app, three notches until set', () => {
    const levels = { Browser: 5 as const };
    expect(levelFor(levels, 'Browser')).toBe(5);
    expect(levelFor(levels, 'Claude')).toBe(3);
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

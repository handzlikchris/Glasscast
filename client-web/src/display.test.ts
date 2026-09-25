import { describe, expect, it } from 'vitest';
import { DEFAULT_BRIGHTNESS, loadBrightness, nextBrightness, parseBrightness } from './display';

describe('brightness', () => {
  it('steps down 100% → 80% → 65% → 50% and wraps back to full', () => {
    expect(nextBrightness(1)).toBe(0.8);
    expect(nextBrightness(0.8)).toBe(0.65);
    expect(nextBrightness(0.65)).toBe(0.5);
    expect(nextBrightness(0.5)).toBe(1);
  });

  it('accepts only its own levels from storage', () => {
    expect(parseBrightness('0.65')).toBe(0.65);
    expect(parseBrightness(null)).toBe(DEFAULT_BRIGHTNESS);
    expect(parseBrightness('3')).toBe(DEFAULT_BRIGHTNESS);
    expect(parseBrightness('bright')).toBe(DEFAULT_BRIGHTNESS);
  });

  it('falls back to the default without storage', () => {
    expect(loadBrightness()).toBe(DEFAULT_BRIGHTNESS);
  });
});

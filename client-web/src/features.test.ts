import { describe, expect, it } from 'vitest';
import { ALL_FEATURES, parseFeatures } from './features';

describe('what the server offers', () => {
  it('takes a relay-only server at its word', () => {
    expect(parseFeatures({ pc: false, phone: true })).toEqual({ pc: false, phone: true });
    expect(parseFeatures({ pc: true, phone: true })).toEqual(ALL_FEATURES);
  });

  it.each([null, 'pc', {}, { pc: 'no', phone: true }, { pc: false, phone: false }])('falls back to both for %j', (data) => {
    expect(parseFeatures(data)).toEqual(ALL_FEATURES);
  });
});

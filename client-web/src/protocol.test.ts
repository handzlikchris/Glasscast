import { describe, expect, it } from 'vitest';
import { parseServerMessage } from './protocol';

describe('parseServerMessage', () => {
  it('accepts well-formed messages', () => {
    expect(parseServerMessage('{"type":"pairCode","code":"ABC-234","expiresInSeconds":60}')).toEqual({
      type: 'pairCode',
      code: 'ABC-234',
      expiresInSeconds: 60,
    });
    expect(parseServerMessage('{"type":"authFailed"}')).toEqual({ type: 'authFailed' });
  });

  it('drops malformed or unknown messages', () => {
    expect(parseServerMessage('not json')).toBeNull();
    expect(parseServerMessage('{"type":"pairCode","code":5}')).toBeNull();
    expect(parseServerMessage('{"type":"exec","cmd":"x"}')).toBeNull();
    expect(parseServerMessage('{"type":"region","region":{"x":"1"}}')).toBeNull();
    expect(parseServerMessage('null')).toBeNull();
  });
});

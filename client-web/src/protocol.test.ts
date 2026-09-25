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

  it('takes the starting mode from hello, falling back to view', () => {
    const hello = (mode: string) =>
      `{"type":"hello","monitor":{"width":2560,"height":1440},"region":{"x":0,"y":0,"width":600,"height":600},"mode":"${mode}","codec":"H264"}`;
    expect(parseServerMessage(hello('pointer'))).toMatchObject({ type: 'hello', mode: 'pointer' });
    expect(parseServerMessage(hello('bogus'))).toMatchObject({ type: 'hello', mode: 'view' });
  });

  it('drops malformed or unknown messages', () => {
    expect(parseServerMessage('not json')).toBeNull();
    expect(parseServerMessage('{"type":"pairCode","code":5}')).toBeNull();
    expect(parseServerMessage('{"type":"exec","cmd":"x"}')).toBeNull();
    expect(parseServerMessage('{"type":"region","region":{"x":"1"}}')).toBeNull();
    expect(parseServerMessage('null')).toBeNull();
  });
});

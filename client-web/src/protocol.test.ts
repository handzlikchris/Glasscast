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

  it('takes app shortcut names from hello, dropping a malformed list', () => {
    const hello = (apps: string) =>
      `{"type":"hello","monitor":{"width":2560,"height":1440},"region":{"x":0,"y":0,"width":600,"height":600},"mode":"pointer","codec":"H264","apps":${apps}}`;
    expect(parseServerMessage(hello('["Claude","Browser"]'))).toMatchObject({ apps: ['Claude', 'Browser'] });
    expect(parseServerMessage(hello('["Claude",5]'))).toMatchObject({ apps: [] });
    expect(parseServerMessage(hello('"Claude"'))).toMatchObject({ apps: [] });
  });

  it('accepts app switch results from the allowlist only', () => {
    expect(parseServerMessage('{"type":"appSwitch","slot":1,"result":"notRunning"}')).toEqual({
      type: 'appSwitch',
      slot: 1,
      result: 'notRunning',
    });
    expect(parseServerMessage('{"type":"appSwitch","slot":1,"result":"launched"}')).toBeNull();
  });

  it('takes the starting mode from hello, falling back to view', () => {
    const hello = (mode: string) =>
      `{"type":"hello","monitor":{"width":2560,"height":1440},"region":{"x":0,"y":0,"width":600,"height":600},"mode":"${mode}","codec":"H264"}`;
    expect(parseServerMessage(hello('pointer'))).toMatchObject({ type: 'hello', mode: 'pointer' });
    expect(parseServerMessage(hello('bogus'))).toMatchObject({ type: 'hello', mode: 'view' });
  });

  it('takes a device token from authenticated only when both parts are there', () => {
    expect(parseServerMessage('{"type":"authenticated","deviceToken":"abc","deviceTokenExpiresAt":1790000000000}')).toEqual({
      type: 'authenticated',
      deviceToken: 'abc',
      deviceTokenExpiresAt: 1790000000000,
    });
    expect(parseServerMessage('{"type":"authenticated"}')).toEqual({ type: 'authenticated' });
    expect(parseServerMessage('{"type":"authenticated","deviceToken":5}')).toEqual({ type: 'authenticated' });
  });

  it('reads media stats with their per-frame timings', () => {
    const stats = (frames: string) =>
      `{"type":"mediaStats","fps":20,"captureMs":6.1,"captureMaxMs":9,"encodeMs":4,"encodeMaxMs":12.5,"kbps":2400,"keyframes":1,"frames":${frames}}`;
    expect(parseServerMessage(stats('[[4500,1790000000000,1234]]'))).toMatchObject({
      type: 'mediaStats',
      fps: 20,
      encodeMaxMs: 12.5,
      frames: [{ rtp: 4500, capturedAt: 1790000000000, bytes: 1234 }],
    });
    expect(parseServerMessage(stats('[[4500,1790000000000]]'))).toBeNull();
    expect(parseServerMessage(stats('[["a",1,2]]'))).toBeNull();
    expect(parseServerMessage(stats(JSON.stringify(Array(121).fill([1, 2, 3]))))).toBeNull();
  });

  it('drops malformed or unknown messages', () => {
    expect(parseServerMessage('not json')).toBeNull();
    expect(parseServerMessage('{"type":"pairCode","code":5}')).toBeNull();
    expect(parseServerMessage('{"type":"exec","cmd":"x"}')).toBeNull();
    expect(parseServerMessage('{"type":"region","region":{"x":"1"}}')).toBeNull();
    expect(parseServerMessage('null')).toBeNull();
  });
});

import { describe, expect, it } from 'vitest';
import { MAX_TEXT_LENGTH, parseServerMessage, textChunks } from './protocol';

describe('textChunks', () => {
  it('splits long text into message-sized pieces in order, with nothing lost', () => {
    const text = 'x'.repeat(MAX_TEXT_LENGTH * 2 + 7);
    const chunks = textChunks(text);
    expect(chunks.map((c) => c.length)).toEqual([MAX_TEXT_LENGTH, MAX_TEXT_LENGTH, 7]);
    expect(chunks.join('')).toBe(text);
    expect(textChunks('short')).toEqual(['short']);
    expect(textChunks('')).toEqual([]);
  });

  it('never cuts an emoji in half', () => {
    expect(textChunks('abcd😀ef', 5)).toEqual(['abcd', '😀ef']);
  });

  it('never splits next to a space, which the PC would trim away', () => {
    const text = Array.from({ length: 130 }, (_, i) => `word${i}`).join(' ');
    const chunks = textChunks(text);
    expect(chunks.join('')).toBe(text);
    expect(chunks.length).toBeGreaterThan(1);
    for (const chunk of chunks) {
      expect(chunk.length).toBeLessThanOrEqual(MAX_TEXT_LENGTH);
      expect(chunk).toBe(chunk.trim());
    }
  });
});

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

  it('says whether the PC offers its sound, only when hello says so', () => {
    const hello = (extra: string) =>
      `{"type":"hello","monitor":{"width":2560,"height":1440},"region":{"x":0,"y":0,"width":600,"height":600},"mode":"pointer","codec":"H264"${extra}}`;
    expect(parseServerMessage(hello(',"audio":true'))).toMatchObject({ audio: true });
    expect(parseServerMessage(hello(''))).toMatchObject({ audio: false });
    expect(parseServerMessage(hello(',"audio":"yes"'))).toMatchObject({ audio: false });
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
      keyframeRequests: 0,
      sendMs: 0,
      nacked: 0,
      frames: [{ rtp: 4500, capturedAt: 1790000000000, bytes: 1234 }],
    });
    expect(parseServerMessage(stats('[]').replace('"frames"', '"sendMs":3.5,"sendMaxMs":40,"nacked":6,"resent":5,"rtcpUnreadable":2,"frames"'))).toMatchObject({
      sendMs: 3.5,
      sendMaxMs: 40,
      nacked: 6,
      resent: 5,
      rtcpUnreadable: 2,
    });
    expect(parseServerMessage(stats('[]'))).toMatchObject({ audioOn: false, audioKbps: 0, audioPackets: 0 });
    expect(parseServerMessage(stats('[]').replace('"frames"', '"audioOn":true,"audioKbps":39.5,"audioPackets":50,"frames"'))).toMatchObject({
      audioOn: true,
      audioKbps: 39.5,
      audioPackets: 50,
    });
    expect(parseServerMessage(stats('[[4500,1790000000000]]'))).toBeNull();
    expect(parseServerMessage(stats('[["a",1,2]]'))).toBeNull();
    expect(parseServerMessage(stats(JSON.stringify(Array(121).fill([1, 2, 3]))))).toBeNull();
  });

  it('parses the phone relay messages', () => {
    expect(parseServerMessage('{"type":"phoneStatus","state":"asking"}')).toEqual({ type: 'phoneStatus', state: 'asking' });
    expect(parseServerMessage('{"type":"phoneStatus","state":"hacked"}')).toBeNull();
    expect(parseServerMessage('{"type":"iceCandidate","candidate":"candidate:1 1 udp 1 10.0.0.2 5 typ host","sdpMid":"0","sdpMLineIndex":0}')).toEqual({
      type: 'iceCandidate',
      candidate: 'candidate:1 1 udp 1 10.0.0.2 5 typ host',
      sdpMid: '0',
      sdpMLineIndex: 0,
    });
    expect(parseServerMessage('{"type":"iceCandidate","candidate":"c"}')).toEqual({ type: 'iceCandidate', candidate: 'c', sdpMid: null, sdpMLineIndex: null });
    expect(parseServerMessage('{"type":"iceCandidate","candidate":5}')).toBeNull();
    expect(parseServerMessage('{"type":"iceCandidate","candidate":"c","sdpMid":7}')).toBeNull();
  });

  it('drops malformed or unknown messages', () => {
    expect(parseServerMessage('not json')).toBeNull();
    expect(parseServerMessage('{"type":"pairCode","code":5}')).toBeNull();
    expect(parseServerMessage('{"type":"exec","cmd":"x"}')).toBeNull();
    expect(parseServerMessage('{"type":"region","region":{"x":"1"}}')).toBeNull();
    expect(parseServerMessage('null')).toBeNull();
  });
});

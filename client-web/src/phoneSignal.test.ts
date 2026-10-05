import { describe, expect, it } from 'vitest';
import { describeRelayClose, parseRelayMessage } from './phoneSignal';

const MAC = 'A'.repeat(43);
const NONCE = 'A'.repeat(22);
const KEY = 'B' + 'A'.repeat(86);

describe('relay messages', () => {
  it('parses what the phone sends through the relay', () => {
    expect(parseRelayMessage('{"type":"phoneStatus","state":"ready"}')).toEqual({ type: 'phoneStatus', state: 'ready' });
    expect(parseRelayMessage(`{"type":"pairKey","key":"${KEY}"}`)).toEqual({ type: 'pairKey', key: KEY });
    expect(parseRelayMessage('{"type":"paired"}')).toEqual({ type: 'paired' });
    expect(parseRelayMessage('{"type":"pairFailed"}')).toEqual({ type: 'pairFailed' });
    expect(parseRelayMessage(`{"type":"challenge","nonce":"${NONCE}","mac":"${MAC}"}`)).toEqual({ type: 'challenge', nonce: NONCE, mac: MAC });
    expect(parseRelayMessage('{"type":"authFailed"}')).toEqual({ type: 'authFailed' });
    expect(parseRelayMessage(`{"type":"rtcOffer","sdp":"v=0","mac":"${MAC}"}`)).toEqual({ type: 'rtcOffer', sdp: 'v=0', mac: MAC });
    expect(parseRelayMessage('{"type":"iceCandidate","candidate":"candidate:1 1 udp 1 10.0.0.2 5 typ host","sdpMid":"0","sdpMLineIndex":0}')).toEqual({
      type: 'iceCandidate',
      candidate: 'candidate:1 1 udp 1 10.0.0.2 5 typ host',
      sdpMid: '0',
      sdpMLineIndex: 0,
    });
    expect(parseRelayMessage('{"type":"iceCandidate","candidate":"c"}')).toEqual({ type: 'iceCandidate', candidate: 'c', sdpMid: null, sdpMLineIndex: null });
    expect(parseRelayMessage('{"type":"pong","t":4}')).toEqual({ type: 'pong', t: 4 });
  });

  it('parses how the server finds the phone', () => {
    expect(parseRelayMessage('{"type":"connectCode","code":"XYZ-789"}')).toEqual({ type: 'connectCode', code: 'XYZ-789' });
    expect(parseRelayMessage(`{"type":"phoneFound","phone":"${NONCE}"}`)).toEqual({ type: 'phoneFound', phone: NONCE });
  });

  it.each([
    '{"type":"phoneStatus","state":"hacked"}',
    '{"type":"pairKey","key":"short"}',
    `{"type":"challenge","nonce":"${NONCE}","mac":"${'+'.repeat(43)}"}`,
    '{"type":"rtcOffer","sdp":"v=0"}',
    '{"type":"iceCandidate","candidate":5}',
    '{"type":"iceCandidate","candidate":"c","sdpMid":7}',
    '{"type":"hello"}',
    '{"type":"connectCode","code":"ABO-234"}',
    '{"type":"connectCode","code":"<b>hi</b>"}',
    '{"type":"phoneFound","phone":"../etc"}',
    'not json',
  ])('drops %s', (raw) => {
    expect(parseRelayMessage(raw)).toBeNull();
  });

  it('explains why the relay closed', () => {
    expect(describeRelayClose('phone offline')).toContain('companion app');
    expect(describeRelayClose('')).toContain('internet');
  });
});

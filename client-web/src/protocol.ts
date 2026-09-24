// Wire protocol with the .NET server. Must stay in sync with
// server/Protocol/ControlProtocol.cs (client → server) and the anonymous
// objects sent from GlassesEndpoints / ControlSession (server → client).

export type ViewMode = 'overview' | 'view' | 'pointer' | 'scroll' | 'type';

export type KeyName =
  | 'Enter'
  | 'Escape'
  | 'Tab'
  | 'Backspace'
  | 'Ctrl+C'
  | 'Ctrl+V'
  | 'Alt+Tab'
  | 'Win+Shift+Left'
  | 'Win+Shift+Right';

export interface Size {
  width: number;
  height: number;
}

/** Region on the primary monitor, in physical pixels. */
export interface Region {
  x: number;
  y: number;
  width: number;
  height: number;
}

export const MAX_TEXT_LENGTH = 500;

export type ServerMessage =
  | { type: 'pairCode'; code: string; expiresInSeconds: number }
  | { type: 'paired'; token: string }
  | { type: 'pairFailed' }
  | { type: 'authFailed' }
  | { type: 'authenticated' }
  | { type: 'hello'; monitor: Size; region: Region; mode: ViewMode; codec: string }
  | { type: 'rtcOffer'; sdp: string }
  | { type: 'region'; region: Region }
  | { type: 'pong'; t: number; serverTime: number };

export type ClientMessage =
  | { type: 'authenticate'; token: string }
  | { type: 'rtcAnswer'; sdp: string }
  | { type: 'iceCandidate'; candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }
  | { type: 'setMode'; mode: ViewMode }
  | { type: 'setRegion'; x: number; y: number; width: number; height: number }
  | { type: 'move'; x: number; y: number }
  | { type: 'click'; button: 'left' }
  | { type: 'scroll'; dy: number }
  | { type: 'typeText'; text: string }
  | { type: 'key'; key: KeyName }
  | { type: 'ping'; t: number };

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const isNumber = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const isString = (v: unknown): v is string => typeof v === 'string';

const isRegion = (v: unknown): v is Region =>
  isObject(v) && isNumber(v.x) && isNumber(v.y) && isNumber(v.width) && isNumber(v.height);

const isSize = (v: unknown): v is Size => isObject(v) && isNumber(v.width) && isNumber(v.height);

/** Validates a message from the server; anything unexpected is dropped. */
export function parseServerMessage(raw: string): ServerMessage | null {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isObject(data) || !isString(data.type)) return null;

  switch (data.type) {
    case 'pairCode':
      return isString(data.code) && isNumber(data.expiresInSeconds)
        ? { type: 'pairCode', code: data.code, expiresInSeconds: data.expiresInSeconds }
        : null;
    case 'paired':
      return isString(data.token) ? { type: 'paired', token: data.token } : null;
    case 'pairFailed':
    case 'authFailed':
    case 'authenticated':
      return { type: data.type };
    case 'hello':
      return isSize(data.monitor) && isRegion(data.region) && isString(data.codec)
        ? { type: 'hello', monitor: data.monitor, region: data.region, mode: 'view', codec: data.codec }
        : null;
    case 'rtcOffer':
      return isString(data.sdp) ? { type: 'rtcOffer', sdp: data.sdp } : null;
    case 'region':
      return isRegion(data.region) ? { type: 'region', region: data.region } : null;
    case 'pong':
      return isNumber(data.t) && isNumber(data.serverTime)
        ? { type: 'pong', t: data.t, serverTime: data.serverTime }
        : null;
    default:
      return null;
  }
}

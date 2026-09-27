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

/** Most characters one typeText message may carry (the PC rejects longer ones). */
export const MAX_TEXT_LENGTH = 500;

/**
 * Splits text of any length into typeText-sized pieces, in order. A split never lands next to
 * whitespace (the PC trims each message, so a space there would be lost and two words would
 * run together) nor between the halves of a surrogate pair (an emoji); it moves back up to half
 * a piece to find such a place, mid-word if need be: the pieces are typed back to back.
 */
export function textChunks(text: string, max = MAX_TEXT_LENGTH): string[] {
  const isSpace = (i: number) => /\s/.test(text[i]);
  const isHighSurrogate = (i: number) => {
    const code = text.charCodeAt(i);
    return code >= 0xd800 && code <= 0xdbff;
  };
  const chunks: string[] = [];
  let start = 0;
  while (start < text.length) {
    let end = Math.min(start + max, text.length);
    if (end < text.length) {
      let split = end;
      while (split > start + max / 2 && (isSpace(split - 1) || isSpace(split) || isHighSurrogate(split - 1))) split--;
      if (split > start + max / 2) end = split;
      else if (isHighSurrogate(end - 1) && end - start > 1) end--;
    }
    chunks.push(text.slice(start, end));
    start = end;
  }
  return chunks;
}

/** App shortcuts are configured on the PC; the glasses only see their names and send a slot. */
export const MAX_APPS = 9;
export const MAX_APP_NAME_LENGTH = 16;

export type AppSwitchResult = 'switched' | 'notRunning' | 'failed';

/** One frame the PC sent: RTP timestamp, capture start (Unix ms, PC clock), encoded bytes. */
export interface SentFrame {
  rtp: number;
  capturedAt: number;
  bytes: number;
}

/** About a second of the PC's frame pump (see FramePump.cs). Times in ms. */
export interface PcMediaStats {
  fps: number;
  captureMs: number;
  captureMaxMs: number;
  encodeMs: number;
  encodeMaxMs: number;
  /** Wait in the PC's pacer until a frame's last packet went out. */
  sendMs: number;
  sendMaxMs: number;
  /** Packets the glasses reported lost (NACK), and how many of those the PC sent again. */
  nacked: number;
  resent: number;
  /** RTCP packets from the glasses the PC couldn't decrypt: their NACKs, PLIs and loss reports went unheard. */
  rtcpUnreadable: number;
  /** The encoder's current target (adapted to the link), the glasses' last REMB and loss report. */
  targetKbps: number | null;
  rembKbps: number | null;
  lossPct: number | null;
  /** The link test's current step, 0 outside the test. */
  linkTestKbps: number;
  kbps: number;
  keyframes: number;
  /** Keyframe requests (PLI/FIR) the PC received from the glasses. */
  keyframeRequests: number;
  frames: SentFrame[];
}

/** More frames than a second at 60 fps would be a malformed stats message. */
const MAX_STATS_FRAMES = 120;

export type ServerMessage =
  | { type: 'pairCode'; code: string; expiresInSeconds: number }
  | { type: 'paired'; token: string }
  | { type: 'pairFailed' }
  | { type: 'authFailed' }
  | { type: 'authenticated'; deviceToken?: string; deviceTokenExpiresAt?: number }
  | { type: 'hello'; monitor: Size; region: Region; mode: ViewMode; codec: string; apps: string[] }
  | { type: 'rtcOffer'; sdp: string }
  | { type: 'region'; region: Region }
  | { type: 'pong'; t: number; serverTime: number }
  | { type: 'appSwitch'; slot: number; result: AppSwitchResult }
  | ({ type: 'mediaStats' } & PcMediaStats);

export type ClientMessage =
  | { type: 'authenticate'; token: string }
  | { type: 'resume'; token: string }
  | { type: 'rtcAnswer'; sdp: string }
  | { type: 'iceCandidate'; candidate: string; sdpMid: string | null; sdpMLineIndex: number | null }
  | { type: 'setMode'; mode: ViewMode }
  | { type: 'setRegion'; x: number; y: number; width: number; height: number }
  | { type: 'move'; x: number; y: number }
  | { type: 'click'; button: 'left' }
  | { type: 'scroll'; dy: number }
  | { type: 'typeText'; text: string }
  | { type: 'key'; key: KeyName }
  | { type: 'ping'; t: number }
  | { type: 'switchApp'; slot: number }
  | ({ type: 'stats' } & StatsReport);

/** The glasses' own figures for the PC's stats log (ControlProtocol.ClientStatsFields). null = not measured. */
export interface StatsReport {
  e2eMs: number | null;
  e2eMaxMs: number | null;
  arrivalMs: number | null;
  arrivalMaxMs: number | null;
  playoutMs: number | null;
  playoutMaxMs: number | null;
  slowestKb: number | null;
  clockErrorMs: number | null;
  framesShown: number;
  fps: number | null;
  jitterBufferMs: number | null;
  decodeMs: number | null;
  kbps: number | null;
  lost: number | null;
  lostTotal: number | null;
  nacks: number | null;
  plis: number | null;
  freezes: number | null;
  dropped: number | null;
  keyframes: number | null;
  /** Network type codes (mediaStats.ts NETWORK_CODES) and the network figures. */
  netType: number | null;
  iceNetType: number | null;
  downlinkMbps: number | null;
  rttMs: number | null;
}

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
      return { type: data.type };
    case 'authenticated':
      // With a device token when the PC remembers these glasses (see connection.ts).
      return isString(data.deviceToken) && isNumber(data.deviceTokenExpiresAt)
        ? { type: 'authenticated', deviceToken: data.deviceToken, deviceTokenExpiresAt: data.deviceTokenExpiresAt }
        : { type: 'authenticated' };
    case 'hello':
      return isSize(data.monitor) && isRegion(data.region) && isString(data.codec)
        ? {
            type: 'hello',
            monitor: data.monitor,
            region: data.region,
            mode: toViewMode(data.mode),
            codec: data.codec,
            apps: toApps(data.apps),
          }
        : null;
    case 'rtcOffer':
      return isString(data.sdp) ? { type: 'rtcOffer', sdp: data.sdp } : null;
    case 'region':
      return isRegion(data.region) ? { type: 'region', region: data.region } : null;
    case 'pong':
      return isNumber(data.t) && isNumber(data.serverTime)
        ? { type: 'pong', t: data.t, serverTime: data.serverTime }
        : null;
    case 'appSwitch':
      return isNumber(data.slot) && isAppSwitchResult(data.result)
        ? { type: 'appSwitch', slot: data.slot, result: data.result }
        : null;
    case 'mediaStats': {
      const numbers = ['fps', 'captureMs', 'captureMaxMs', 'encodeMs', 'encodeMaxMs', 'kbps', 'keyframes'] as const;
      const frames = toSentFrames(data.frames);
      if (!numbers.every((k) => isNumber(data[k])) || !frames) return null;
      const n = (k: (typeof numbers)[number]) => data[k] as number;
      return {
        type: 'mediaStats',
        fps: n('fps'),
        captureMs: n('captureMs'),
        captureMaxMs: n('captureMaxMs'),
        encodeMs: n('encodeMs'),
        encodeMaxMs: n('encodeMaxMs'),
        kbps: n('kbps'),
        keyframes: n('keyframes'),
        // Older servers don't send it.
        keyframeRequests: isNumber(data.keyframeRequests) ? data.keyframeRequests : 0,
        sendMs: isNumber(data.sendMs) ? data.sendMs : 0,
        sendMaxMs: isNumber(data.sendMaxMs) ? data.sendMaxMs : 0,
        nacked: isNumber(data.nacked) ? data.nacked : 0,
        resent: isNumber(data.resent) ? data.resent : 0,
        rtcpUnreadable: isNumber(data.rtcpUnreadable) ? data.rtcpUnreadable : 0,
        targetKbps: isNumber(data.targetKbps) ? data.targetKbps : null,
        rembKbps: isNumber(data.rembKbps) ? data.rembKbps : null,
        lossPct: isNumber(data.lossPct) ? data.lossPct : null,
        linkTestKbps: isNumber(data.linkTestKbps) ? data.linkTestKbps : 0,
        frames,
      };
    }
    default:
      return null;
  }
}

const VIEW_MODES: readonly ViewMode[] = ['overview', 'view', 'pointer', 'scroll', 'type'];

/** The server's starting mode; anything unexpected falls back to View, which sends no input. */
function toViewMode(value: unknown): ViewMode {
  return VIEW_MODES.find((m) => m === value) ?? 'view';
}

const isAppSwitchResult = (v: unknown): v is AppSwitchResult =>
  v === 'switched' || v === 'notRunning' || v === 'failed';

/** App names from hello; anything malformed means no shortcuts rather than a broken session. */
function toApps(value: unknown): string[] {
  if (!Array.isArray(value) || value.length > MAX_APPS) return [];
  const ok = value.every((n) => isString(n) && n.length > 0 && n.length <= MAX_APP_NAME_LENGTH);
  return ok ? (value as string[]) : [];
}

/** The frames list of mediaStats: [rtp, capturedAt, bytes] triples. */
function toSentFrames(value: unknown): SentFrame[] | null {
  if (!Array.isArray(value) || value.length > MAX_STATS_FRAMES) return null;
  const frames: SentFrame[] = [];
  for (const f of value) {
    if (!Array.isArray(f) || f.length !== 3 || !f.every(isNumber)) return null;
    frames.push({ rtp: f[0], capturedAt: f[1], bytes: f[2] });
  }
  return frames;
}

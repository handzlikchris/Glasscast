// The way to the phone before a WebRTC connection exists: /ws/session opened with {type:"phone"},
// which the PC relays to the phone's companion app (server PhoneRelay.cs). The glasses' page
// can only open secure connections to a public name, and the phone has neither a name nor a
// certificate, so this first meeting has to happen on a server both can reach. The PC decides
// nothing here: the phone pairs and checks the glasses (phoneTrust.ts). Once the video is up the
// glasses close the relay and the session carries on without the PC.
//
// Must stay in sync with server/Phone/RelayProtocol.cs and CompanionProtocol.cs.

/** Where the phone is: offline (companion not connected), ready (reached), asking (consent dialog), live (capturing). */
export type PhoneState = 'offline' | 'ready' | 'asking' | 'live';

export interface IceCandidate {
  candidate: string;
  sdpMid: string | null;
  sdpMLineIndex: number | null;
}

export type FromRelay =
  | { type: 'phoneStatus'; state: PhoneState }
  | { type: 'pairKey'; key: string }
  | { type: 'paired' }
  | { type: 'pairFailed' }
  | { type: 'challenge'; nonce: string; mac: string }
  | { type: 'authFailed' }
  | { type: 'rtcOffer'; sdp: string; mac: string }
  | ({ type: 'iceCandidate' } & IceCandidate)
  | { type: 'pong'; t: number };

export type ToRelay =
  | { type: 'phone' }
  | { type: 'pairStart'; commit: string }
  | { type: 'pairReveal'; key: string }
  | { type: 'hello'; id: string; nonce: string }
  | { type: 'proof'; mac: string }
  | { type: 'rtcAnswer'; sdp: string; mac: string }
  | ({ type: 'iceCandidate' } & IceCandidate)
  | { type: 'ping'; t: number };

const isObject = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null;
const isString = (v: unknown): v is string => typeof v === 'string';
const isNumber = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const B64U = /^[A-Za-z0-9_-]+$/;
const isB64u = (v: unknown, length: number): v is string => isString(v) && v.length === length && B64U.test(v);

/** Validates a relayed message; anything unexpected is dropped. */
export function parseRelayMessage(raw: string): FromRelay | null {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!isObject(data)) return null;
  switch (data.type) {
    case 'phoneStatus':
      return data.state === 'offline' || data.state === 'ready' || data.state === 'asking' || data.state === 'live'
        ? { type: 'phoneStatus', state: data.state }
        : null;
    case 'pairKey':
      return isB64u(data.key, 87) ? { type: 'pairKey', key: data.key } : null;
    case 'paired':
    case 'pairFailed':
    case 'authFailed':
      return { type: data.type };
    case 'challenge':
      return isB64u(data.nonce, 22) && isB64u(data.mac, 43) ? { type: 'challenge', nonce: data.nonce, mac: data.mac } : null;
    case 'rtcOffer':
      return isString(data.sdp) && isB64u(data.mac, 43) ? { type: 'rtcOffer', sdp: data.sdp, mac: data.mac } : null;
    case 'iceCandidate':
      return isString(data.candidate) &&
        (data.sdpMid === null || data.sdpMid === undefined || isString(data.sdpMid)) &&
        (data.sdpMLineIndex === null || data.sdpMLineIndex === undefined || isNumber(data.sdpMLineIndex))
        ? {
            type: 'iceCandidate',
            candidate: data.candidate,
            sdpMid: isString(data.sdpMid) ? data.sdpMid : null,
            sdpMLineIndex: isNumber(data.sdpMLineIndex) ? data.sdpMLineIndex : null,
          }
        : null;
    case 'pong':
      return isNumber(data.t) ? { type: 'pong', t: data.t } : null;
    default:
      return null;
  }
}

/** Why the PC closed a relay, for the ended screen. */
export function describeRelayClose(reason: string): string {
  switch (reason) {
    case 'phone offline':
      return "The phone's companion app isn't connected. Open it on the phone, then Reconnect.";
    case 'phone declined':
      return 'Screen sharing was cancelled on the phone.';
    case 'phone ended':
      return 'The phone stopped before the video started (Stop, or the phone locked).';
    case 'replaced':
      return 'These glasses connected to the phone again in another window.';
    case 'timeout':
      return "The phone didn't answer in time. Tap Start on the phone's prompt next time.";
    case 'rate limit':
      return 'Too many attempts just now. Wait a minute, then Reconnect.';
    case 'invalid message':
      return 'The PC closed the connection (unexpected messages).';
    default:
      return "Couldn't reach the phone through the server. Check the internet, then Reconnect.";
  }
}

export interface RelayHandlers {
  onMessage(message: FromRelay): void;
  /** The relay closed without us closing it: the PC's close reason. */
  onClose(reason: string): void;
}

export interface RelaySocket {
  send(message: ToRelay): void;
  close(): void;
}

const socketUrl = () => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/session`;

/** Opens the relay to the phone on the server that served this page. */
export function openRelay(handlers: RelayHandlers): RelaySocket {
  const ws = new WebSocket(socketUrl());
  let closedByUs = false;
  ws.onopen = () => ws.send(JSON.stringify({ type: 'phone' } satisfies ToRelay));
  ws.onmessage = (event) => {
    const message = parseRelayMessage(String(event.data));
    if (message) handlers.onMessage(message);
  };
  ws.onclose = (event) => {
    if (!closedByUs) handlers.onClose(event.reason);
  };
  return {
    send(message) {
      if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(message));
    },
    close() {
      closedByUs = true;
      ws.close();
    },
  };
}

// Pairing and session sockets.
//
// Two credentials live only in this module, never in React state, a URL or a log:
//
// - The approval token: in memory from the moment the PC approves pairing until it is sent as
//   the first message of the session socket, then dropped.
// - The device token: after an approval the PC remembers these glasses for a while (24 h) and
//   gives them a device token, so a restart of the page or a lost connection can start a new
//   session without another approval. It is kept in localStorage (the page has a strict CSP and
//   no third-party code), swapped for a new one on every use, and dropped when it expires or the
//   PC closes a session for breaking the rules (the PC forgets the glasses then too). A refused
//   resume keeps it: the PC answers "busy" (a pairing waiting on the PC) exactly like "unknown
//   token", and a token it really no longer knows just fails again until it expires.
import { parseServerMessage, type ClientMessage, type ServerMessage } from './protocol';

let heldToken: string | null = null;

const DEVICE_KEY = 'glassesRemote.device';

interface RememberedDevice {
  token: string;
  expiresAt: number;
}

function loadDevice(): RememberedDevice | null {
  try {
    const raw = localStorage.getItem(DEVICE_KEY);
    if (!raw) return null;
    const device = JSON.parse(raw) as Partial<RememberedDevice>;
    if (typeof device.token === 'string' && typeof device.expiresAt === 'number' && device.expiresAt > Date.now()) {
      return { token: device.token, expiresAt: device.expiresAt };
    }
  } catch {
    // Unreadable or blocked storage: act as if nothing is remembered.
  }
  forgetDevice();
  return null;
}

function saveDevice(device: RememberedDevice): void {
  try {
    localStorage.setItem(DEVICE_KEY, JSON.stringify(device));
  } catch {
    // Can't remember it: the next session simply needs an approval.
  }
}

/** Drops the device token: the next session needs an approval on the PC. */
export function forgetDevice(): void {
  try {
    localStorage.removeItem(DEVICE_KEY);
  } catch {
    // Nothing stored or storage blocked.
  }
}

/** The PC remembers these glasses: a session can start without pairing. */
export function isDeviceRemembered(): boolean {
  return loadDevice() !== null;
}

const socketUrl = (path: string) => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}${path}`;

export interface PairingCallbacks {
  onCode(code: string, expiresInSeconds: number): void;
}

/**
 * Asks the PC to pair. Resolves when you click Approve on the PC; rejects on
 * rejection, timeout, a busy PC or a network problem (the server doesn't say which).
 */
export function requestPairing(callbacks: PairingCallbacks, signal?: AbortSignal): Promise<void> {
  heldToken = null;

  return new Promise((resolve, reject) => {
    const ws = new WebSocket(socketUrl('/ws/pair'));
    let settled = false;

    const finish = (error?: string) => {
      if (settled) return;
      settled = true;
      if (error) reject(new Error(error));
      else resolve();
      if (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING) ws.close();
    };

    signal?.addEventListener('abort', () => finish('Pairing cancelled'));

    ws.onmessage = (event) => {
      const message = parseServerMessage(String(event.data));
      if (!message) return;
      if (message.type === 'pairCode') {
        callbacks.onCode(message.code, message.expiresInSeconds);
      } else if (message.type === 'paired') {
        heldToken = message.token;
        finish();
      } else if (message.type === 'pairFailed') {
        finish('Pairing was not approved');
      }
    };
    ws.onerror = () => finish('Could not reach the PC');
    ws.onclose = () => finish('Pairing ended without approval');
  });
}

export interface SessionHandlers {
  onMessage(message: ServerMessage): void;
  onClose(reason: string): void;
}

/** Authenticated control socket. Open it right after pairing succeeds, or with a remembered device. */
export class Session {
  private readonly ws: WebSocket;
  private closedByUs = false;

  private constructor(first: ClientMessage, private readonly handlers: SessionHandlers) {
    const resuming = first.type === 'resume';
    this.ws = new WebSocket(socketUrl('/ws/session'));
    this.ws.onopen = () => {
      // Either token is used exactly once; the server keeps only hashes.
      this.ws.send(JSON.stringify(first));
    };
    this.ws.onmessage = (event) => {
      const message = parseServerMessage(String(event.data));
      if (message?.type === 'authFailed') {
        this.close();
        // Not forgetDevice(): the refusal may only mean the PC was busy (see the top of this file).
        this.handlers.onClose(
          resuming
            ? "The PC didn't take these glasses back just now. Reconnect to try again, or pair again."
            : 'The PC refused the session. Pair again.',
        );
      } else if (message?.type === 'authenticated') {
        if (message.deviceToken && message.deviceTokenExpiresAt) {
          saveDevice({ token: message.deviceToken, expiresAt: message.deviceTokenExpiresAt });
        }
        this.handlers.onMessage({ type: 'authenticated' });
      } else if (message) {
        this.handlers.onMessage(message);
      }
    };
    this.ws.onclose = (event) => {
      // The PC forgets the glasses when it catches bad messages. Ending the session on the PC
      // ('terminated') keeps them remembered: Reconnect works without a new approval.
      if (['invalid message', 'rate limit', 'bad answer'].includes(event.reason)) forgetDevice();
      if (!this.closedByUs) this.handlers.onClose(describeClose(event));
    };
  }

  /**
   * Uses the token from the last successful pairing, or else the remembered device's token. PC
   * sessions only: the phone pairs and checks the glasses itself (phoneConnect.ts).
   */
  static open(handlers: SessionHandlers): Session {
    const token = heldToken;
    heldToken = null;
    if (token) return new Session({ type: 'authenticate', token }, handlers);
    const device = loadDevice();
    if (device) return new Session({ type: 'resume', token: device.token }, handlers);
    throw new Error('Not paired');
  }

  send(message: ClientMessage): void {
    if (this.ws.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(message));
  }

  close(): void {
    this.closedByUs = true;
    this.ws.close();
  }
}

function describeClose(event: CloseEvent): string {
  switch (event.reason) {
    case 'terminated':
      return 'The session was ended on the PC.';
    case 'replaced':
      return 'These glasses connected again in another session.';
    case 'idle':
      return 'The session ended after a long time without input.';
    case 'no heartbeat':
      return 'The PC heard nothing from the glasses for a while and closed the session.';
    case 'rate limit':
    case 'invalid message':
      return 'The PC closed the session (unexpected messages).';
    case 'media error':
      return "The PC's video stopped with an error. Reconnect to try again.";
    case 'server error':
      return 'The PC hit an error and closed the session. Reconnect to try again.';
    default:
      return 'The connection to the PC was lost.';
  }
}

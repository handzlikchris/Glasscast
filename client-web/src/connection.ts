// Pairing and session sockets.
//
// The session token only ever lives in this module, in memory, between the
// moment the PC approves pairing and the moment it is sent as the first
// message of the session socket. It is never put in React state, a URL,
// localStorage, sessionStorage, or a log, and it is dropped as soon as it has
// been sent. Every new session needs a fresh pairing.
import { parseServerMessage, type ClientMessage, type ServerMessage } from './protocol';

let heldToken: string | null = null;

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

/** Authenticated control socket. Open it right after pairing succeeds. */
export class Session {
  private readonly ws: WebSocket;
  private closedByUs = false;

  private constructor(token: string, private readonly handlers: SessionHandlers) {
    this.ws = new WebSocket(socketUrl('/ws/session'));
    this.ws.onopen = () => {
      // The token is used exactly once and then only the server's hash remains.
      this.ws.send(JSON.stringify({ type: 'authenticate', token } satisfies ClientMessage));
    };
    this.ws.onmessage = (event) => {
      const message = parseServerMessage(String(event.data));
      if (message?.type === 'authFailed') {
        this.close();
        this.handlers.onClose('The PC refused the session. Pair again.');
      } else if (message) {
        this.handlers.onMessage(message);
      }
    };
    this.ws.onclose = (event) => {
      if (!this.closedByUs) this.handlers.onClose(describeClose(event));
    };
  }

  /** Consumes the token from the last successful pairing. */
  static open(handlers: SessionHandlers): Session {
    const token = heldToken;
    heldToken = null;
    if (!token) throw new Error('Not paired');
    return new Session(token, handlers);
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
    case 'idle':
      return 'The session ended after a long time without input.';
    case 'rate limit':
    case 'invalid message':
      return 'The PC closed the session (unexpected messages).';
    default:
      return 'The connection to the PC was lost.';
  }
}

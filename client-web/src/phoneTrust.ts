// Pairing these glasses with the phone, and proving each other on every session
// (architecture/phone-mode.md, "Pairing and authentication"). The PC only passes these messages
// between the glasses and the phone's companion: it never holds a key, and a PC (or anyone in
// between) that tampered with them would show a different code on the two screens.
//
// Pairing, a numeric comparison with a commitment:
//   glasses → phone  pairStart{commit = SHA-256(glasses' public key)}
//   phone → glasses  pairKey{key}
//   glasses → phone  pairReveal{key}     (the phone checks it against the commitment)
//   both: Z = ECDH (P-256), prk = HMAC("glasses-remote/pair/v1", Z‖gPub‖pPub),
//         code = 6 digits of HMAC(prk, "code"), key = HMAC(prk, "key"), id = HMAC(prk, "id")[0..12]
//   You compare the codes and tap Approve on the phone; the phone then says "paired".
//   The commitment makes the glasses choose their key before seeing the phone's, so a relay in
//   the middle can't search for two keys that happen to give the same code.
//
// A session: hello{id, nonce} → challenge{nonce, mac} (the phone's proof) → proof{mac} (ours);
// sk = HMAC(key, "session"‖gNonce‖pNonce). The phone's offer and our answer carry
// HMAC(sk, "offer\n"‖sdp) / HMAC(sk, "answer\n"‖sdp), which ties the WebRTC connection (its
// DTLS fingerprints are in the SDP) to the paired phone.
//
// The pairing key lives in localStorage next to the PC's device token (strict CSP, no third-party
// code), never in React state, a URL or a log. Web Crypto needs a secure context (HTTPS).

const PAIRING_KEY = 'glassesRemote.phone';
const PAIR_LABEL = 'glasses-remote/pair/v1';

export interface PhonePairing {
  /** Which pairing this is, so the phone finds the key (base64url, 12 bytes). */
  id: string;
  /** The shared key (base64url, 32 bytes). */
  key: string;
}

// ---- storage ----

export function loadPairing(): PhonePairing | null {
  try {
    const raw = localStorage.getItem(PAIRING_KEY);
    if (!raw) return null;
    const stored = JSON.parse(raw) as Partial<PhonePairing>;
    if (typeof stored.id === 'string' && typeof stored.key === 'string') return { id: stored.id, key: stored.key };
  } catch {
    // Unreadable or blocked storage: not paired.
  }
  return null;
}

function savePairing(pairing: PhonePairing): void {
  try {
    localStorage.setItem(PAIRING_KEY, JSON.stringify(pairing));
  } catch {
    // Can't keep it: the next session pairs again.
  }
}

/** Forgets the phone: the next phone session pairs again (approved on the phone). */
export function forgetPairing(): void {
  try {
    localStorage.removeItem(PAIRING_KEY);
  } catch {
    // Nothing stored or storage blocked.
  }
}

// ---- encoding and primitives ----

export function toB64u(bytes: Uint8Array): string {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function fromB64u(text: string): Uint8Array<ArrayBuffer> {
  const binary = atob(text.replace(/-/g, '+').replace(/_/g, '/'));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

const utf8 = (text: string) => new TextEncoder().encode(text);

function concat(...parts: Uint8Array[]): Uint8Array<ArrayBuffer> {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

async function hmac(key: Uint8Array, ...parts: Uint8Array[]): Promise<Uint8Array> {
  const k = await crypto.subtle.importKey('raw', concat(key), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', k, concat(...parts)));
}

/** Compares without stopping at the first difference. */
function same(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

const random = (n: number) => crypto.getRandomValues(new Uint8Array(n));

// ---- pairing ----

export interface PairingResult {
  /** Six digits, shown on the glasses and the phone. */
  code: string;
  pairing: PhonePairing;
}

/** What both sides derive from the ECDH secret and the two public keys. */
export async function derivePairing(z: Uint8Array, glassesKey: Uint8Array, phoneKey: Uint8Array): Promise<PairingResult> {
  const prk = await hmac(utf8(PAIR_LABEL), z, glassesKey, phoneKey);
  const codeBytes = await hmac(prk, utf8('code'));
  const code = new DataView(codeBytes.buffer).getUint32(0) % 1_000_000;
  return {
    code: String(code).padStart(6, '0'),
    pairing: { id: toB64u((await hmac(prk, utf8('id'))).slice(0, 12)), key: toB64u(await hmac(prk, utf8('key'))) },
  };
}

/** The glasses' half of pairing: a fresh key pair, committed to before the phone's key is seen. */
export class PairingExchange {
  private constructor(
    private readonly keys: CryptoKeyPair,
    private readonly publicKey: Uint8Array,
  ) {}

  static async start(keys?: CryptoKeyPair): Promise<PairingExchange> {
    const pair = keys ?? (await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']));
    const raw = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
    return new PairingExchange(pair, raw);
  }

  /** pairStart's commitment: SHA-256 of our public key. */
  async commit(): Promise<string> {
    return toB64u(new Uint8Array(await crypto.subtle.digest('SHA-256', concat(this.publicKey))));
  }

  /** With the phone's key: our key for pairReveal, the code to show, and the pairing to keep once approved. */
  async reveal(phoneKey: string): Promise<PairingResult & { key: string }> {
    const phoneRaw = fromB64u(phoneKey);
    const phone = await crypto.subtle.importKey('raw', concat(phoneRaw), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const z = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: phone }, this.keys.privateKey, 256));
    return { key: toB64u(this.publicKey), ...(await derivePairing(z, this.publicKey, phoneRaw)) };
  }

  /** The phone approved: keep the pairing. */
  static keep(pairing: PhonePairing): void {
    savePairing(pairing);
  }
}

// ---- a session ----

export async function sessionKey(key: string, glassesNonce: string, phoneNonce: string): Promise<Uint8Array> {
  return hmac(fromB64u(key), utf8('session'), fromB64u(glassesNonce), fromB64u(phoneNonce));
}

export async function labelMac(sk: Uint8Array, label: 'phone' | 'glasses'): Promise<string> {
  return toB64u(await hmac(sk, utf8(label)));
}

export async function sdpMac(sk: Uint8Array, kind: 'offer' | 'answer', sdp: string): Promise<string> {
  return toB64u(await hmac(sk, utf8(`${kind}\n`), utf8(sdp)));
}

/**
 * The glasses' side of proving each other: hello, then check the phone's challenge and give our
 * proof, then check the offer and sign the answer.
 */
export class Handshake {
  readonly nonce = toB64u(random(16));
  private sk: Uint8Array | null = null;

  constructor(private readonly pairing: PhonePairing) {}

  hello() {
    return { type: 'hello' as const, id: this.pairing.id, nonce: this.nonce };
  }

  /** Our proof, or null when the phone didn't prove it holds the key (not our phone). */
  async answerChallenge(phoneNonce: string, mac: string): Promise<string | null> {
    const sk = await sessionKey(this.pairing.key, this.nonce, phoneNonce);
    if (!same(fromB64u(await labelMac(sk, 'phone')), fromB64u(mac))) return null;
    this.sk = sk;
    return labelMac(sk, 'glasses');
  }

  /** Whether the offer came from the phone we proved (false before the challenge). */
  async offerIsGenuine(sdp: string, mac: string): Promise<boolean> {
    return this.sk !== null && same(fromB64u(await sdpMac(this.sk, 'offer', sdp)), fromB64u(mac));
  }

  async answerMac(sdp: string): Promise<string> {
    if (!this.sk) throw new Error('not authenticated');
    return sdpMac(this.sk, 'answer', sdp);
  }
}

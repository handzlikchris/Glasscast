import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PhoneConnector, type ConnectEvents } from './phoneConnect';
import type { FromRelay, RelayHandlers, ToRelay } from './phoneSignal';
import { loadPhoneId } from './phoneRoute';
import { derivePairing, fromB64u, labelMac, loadPairing, sdpMac, sessionKey, toB64u, type PhonePairing } from './phoneTrust';

const OFFER = 'v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\na=fingerprint:sha-256 AA\r\n';
const ANSWER = 'v=0\r\no=- 3 4 IN IP4 127.0.0.1\r\n';
const PHONE_ID = 'p'.repeat(21) + '_';

/** The companion's side, as GlassesRelay.kt does it: pairs (approving), then proves itself and offers. */
class FakePhone {
  pairing: PhonePairing | null = null;
  code: string | null = null;
  received: ToRelay[] = [];
  answerOk: boolean | null = null;
  /** Sign offers with the wrong key (a phone that isn't ours). */
  forge = false;
  /** The server doesn't know which phone: glasses without its id get a connect code. */
  needsCode = false;
  /** The phone id the glasses opened the relay with. */
  askedFor: string | undefined;
  private commit = '';
  private keys: CryptoKeyPair | null = null;
  private publicKey = new Uint8Array();
  private sk: Uint8Array | null = null;
  send: (message: FromRelay) => void = () => {};

  async receive(message: ToRelay): Promise<void> {
    this.received.push(message);
    switch (message.type) {
      case 'phone':
        this.askedFor = message.phone;
        if (this.needsCode && message.phone !== PHONE_ID) {
          this.send({ type: 'connectCode', code: 'ABC-234' });
        } else {
          this.send({ type: 'phoneStatus', state: 'ready' });
        }
        break;
      case 'pairStart':
        this.commit = message.commit;
        this.keys = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
        this.publicKey = new Uint8Array(await crypto.subtle.exportKey('raw', this.keys.publicKey));
        this.send({ type: 'pairKey', key: toB64u(this.publicKey) });
        break;
      case 'pairReveal': {
        const glassesKey = fromB64u(message.key);
        const hash = toB64u(new Uint8Array(await crypto.subtle.digest('SHA-256', glassesKey)));
        if (hash !== this.commit) {
          this.send({ type: 'pairFailed' });
          return;
        }
        const glasses = await crypto.subtle.importKey('raw', glassesKey, { name: 'ECDH', namedCurve: 'P-256' }, false, []);
        const z = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: glasses }, this.keys!.privateKey, 256));
        const result = await derivePairing(z, glassesKey, this.publicKey);
        this.code = result.code;
        this.pairing = result.pairing;
        this.send({ type: 'paired' });
        break;
      }
      case 'hello': {
        if (!this.pairing || message.id !== this.pairing.id) {
          this.send({ type: 'authFailed' });
          return;
        }
        const nonce = toB64u(crypto.getRandomValues(new Uint8Array(16)));
        this.sk = await sessionKey(this.pairing.key, message.nonce, nonce);
        const sk = this.forge ? await sessionKey(toB64u(new Uint8Array(32)), message.nonce, nonce) : this.sk;
        this.send({ type: 'challenge', nonce, mac: await labelMac(sk, 'phone') });
        break;
      }
      case 'proof':
        if (message.mac !== (await labelMac(this.sk!, 'glasses'))) return;
        this.send({ type: 'phoneStatus', state: 'asking' });
        this.send({ type: 'phoneStatus', state: 'live' });
        this.send({ type: 'rtcOffer', sdp: OFFER, mac: await sdpMac(this.sk!, 'offer', OFFER) });
        this.send({ type: 'iceCandidate', candidate: 'candidate:1 1 udp 1 192.168.1.233 40000 typ host', sdpMid: '0', sdpMLineIndex: 0 });
        break;
      case 'rtcAnswer':
        this.answerOk = message.mac === (await sdpMac(this.sk!, 'answer', message.sdp));
        break;
    }
  }
}

/** Someone typed the code into the companion: the server says which phone, then it's reached. */
function claim(phone: FakePhone) {
  phone.send({ type: 'phoneFound', phone: PHONE_ID });
  phone.send({ type: 'phoneStatus', state: 'ready' });
}

function connect(phone: FakePhone, pairAgain = false) {
  const events = {
    onConnectCode: vi.fn(),
    onPhone: vi.fn(),
    onCode: vi.fn(),
    onOffer: vi.fn(async () => ANSWER),
    onCandidate: vi.fn(),
    onPong: vi.fn(),
    onFailed: vi.fn(),
  } satisfies ConnectEvents;
  let handlers: RelayHandlers | null = null;
  const closed = vi.fn();
  const connector = new PhoneConnector(
    (h, phoneId) => {
      handlers = h;
      phone.send = (message) => setTimeout(() => h.onMessage(message));
      setTimeout(() => void phone.receive(phoneId ? { type: 'phone', phone: phoneId } : { type: 'phone' }));
      return { send: (message) => setTimeout(() => void phone.receive(message)), close: closed };
    },
    events,
    pairAgain,
  );
  return { connector, events, closed, closeFromPc: (reason: string) => handlers!.onClose(reason) };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 400));

beforeEach(() => {
  const store = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => store.set(k, v),
    removeItem: (k: string) => store.delete(k),
  });
});

describe('connecting to the phone', () => {
  it('pairs first, showing the code the phone shows, then proves both sides and signs the answer', async () => {
    const phone = new FakePhone();
    const { connector, events } = connect(phone);
    connector.sendCandidate({ candidate: 'candidate:early', sdpMid: '0', sdpMLineIndex: 0 });
    await settle();

    expect(events.onCode).toHaveBeenCalledWith(phone.code);
    expect(events.onCode).toHaveBeenLastCalledWith(null);
    expect(loadPairing()).toEqual(phone.pairing);
    expect(events.onPhone).toHaveBeenCalledWith('live');
    expect(events.onOffer).toHaveBeenCalledWith(OFFER);
    expect(events.onCandidate).toHaveBeenCalledWith(expect.objectContaining({ candidate: expect.stringContaining('192.168.1.233') }));
    expect(phone.answerOk).toBe(true);
    // Our early candidate went only after the answer.
    const types = phone.received.map((m) => m.type);
    expect(types.indexOf('iceCandidate')).toBeGreaterThan(types.indexOf('rtcAnswer'));
    expect(events.onFailed).not.toHaveBeenCalled();
  });

  it('goes straight to the handshake when already paired', async () => {
    const phone = new FakePhone();
    connect(phone);
    await settle();
    phone.received = [];

    const again = connect(phone);
    await settle();
    expect(phone.received.map((m) => m.type)).not.toContain('pairStart');
    expect(again.events.onCode).not.toHaveBeenCalledWith(expect.any(String));
    expect(phone.answerOk).toBe(true);
  });

  it('pairs again when the phone has forgotten these glasses', async () => {
    const phone = new FakePhone();
    connect(phone);
    await settle();
    const first = loadPairing();
    phone.pairing = null;

    const again = connect(phone);
    await settle();
    expect(again.events.onCode).toHaveBeenCalledWith(phone.code);
    expect(loadPairing()).not.toEqual(first);
    expect(phone.answerOk).toBe(true);
  });

  it('refuses a phone that cannot prove the pairing', async () => {
    const phone = new FakePhone();
    connect(phone);
    await settle();
    phone.forge = true;

    const again = connect(phone);
    await settle();
    expect(again.events.onFailed).toHaveBeenCalledWith(expect.stringContaining("wasn't your phone"));
    expect(again.events.onOffer).not.toHaveBeenCalled();
  });

  it('shows a connect code until the phone is found, then asks for that phone by id', async () => {
    const phone = new FakePhone();
    phone.needsCode = true;
    const first = connect(phone);
    await settle();
    expect(first.events.onConnectCode).toHaveBeenLastCalledWith('ABC-234');
    expect(phone.received.map((m) => m.type)).toEqual(['phone']);

    claim(phone);
    await settle();
    expect(first.events.onConnectCode).toHaveBeenLastCalledWith(null);
    expect(loadPhoneId()).toBe(PHONE_ID);
    expect(phone.answerOk).toBe(true);

    const again = connect(phone);
    await settle();
    expect(phone.askedFor).toBe(PHONE_ID);
    expect(again.events.onConnectCode).not.toHaveBeenCalledWith(expect.any(String));
  });

  it('says nobody typed the code when the relay times out showing it', async () => {
    const phone = new FakePhone();
    phone.needsCode = true;
    const waiting = connect(phone);
    await settle();
    waiting.closeFromPc('timeout');
    expect(waiting.events.onFailed).toHaveBeenCalledWith(expect.stringContaining('Connect glasses'));
    expect(waiting.events.onConnectCode).toHaveBeenLastCalledWith(null);
  });

  it('finds the phone again after Pair again', async () => {
    const phone = new FakePhone();
    phone.needsCode = true;
    connect(phone);
    await settle();
    claim(phone);
    await settle();

    const again = connect(phone, true);
    await settle();
    expect(phone.askedFor).toBeUndefined();
    expect(again.events.onConnectCode).toHaveBeenLastCalledWith('ABC-234');
  });

  it('ends when the relay closes before the video is up, but not after', async () => {
    const phone = new FakePhone();
    const early = connect(phone);
    early.closeFromPc('phone declined');
    expect(early.events.onFailed).toHaveBeenCalledWith('Screen sharing was cancelled on the phone.');

    const later = connect(phone);
    await settle();
    later.connector.connected();
    expect(later.closed).toHaveBeenCalled();
    later.closeFromPc('relay ended');
    expect(later.events.onFailed).not.toHaveBeenCalled();
  });
});

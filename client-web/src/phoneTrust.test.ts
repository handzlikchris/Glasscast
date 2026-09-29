import { describe, expect, it } from 'vitest';
import { derivePairing, fromB64u, Handshake, labelMac, PairingExchange, sdpMac, sessionKey, toB64u } from './phoneTrust';

// The same vector is checked by the companion's GlassesTrustTest.kt: both sides must derive the
// same code, key, id and MACs (generated once with Node's crypto).
const V = {
  dG: 'ERERERERERERERERERERERERERERERERERERERERERE',
  dP: 'IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI',
  gPub: 'BAIX5hfwtkQ5KCePlpmeaaI6TywVK99tbN9m5bgCgtTtGUp968uXcS0t2jyoWqh2Wlb0X8dYWZZS8ol8ZTBuV5Q',
  pPub: 'BNZak5d8qj0bCBhS_1ennkZfFmBXcwS66tUF3TpIWJzzUBheiVNy32Ih6joTdVfkc_3bZ1XwW9UHw8Uz_OnJEoU',
  z: 'zPwmH1gZPJjKStSlO7rG8O4pvE1IQ4CQRGkIYiynmvY',
  commit: 'K60P1hDZnq5EPpMqJhQryh5fqZW0UYRSgn547x8xf_A',
  code: '752138',
  key: '2rZ3D8DKYzvJGm7ap1t99fPIpg7gyA1oaCtLy81N734',
  id: 'e7OWfrvMTctc7H2n',
  gNonce: 'MzMzMzMzMzMzMzMzMzMzMw',
  pNonce: 'RERERERERERERERERERERA',
  phoneMac: '8kaJJpg5ZFDDEx2mYJXnoXEs6kX0KSCzmN2-G9JReeE',
  glassesMac: 'iRMu34EQiaj6y2JJ7TeSteLdEFjFNmK3quM8-6HTYVk',
  sdp: 'v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\n',
  offerMac: 'm1tV9PeYr9t7ILJdrnakF4xUwa5KzeWJkI43l5o_5hY',
  answerMac: '2pwIizM2lmNqQdeOT61Hmp3zdDgucN3DnbSEz3DU-U8',
};

/** The vector's glasses key pair, as Web Crypto keys. */
async function vectorKeys(): Promise<CryptoKeyPair> {
  const pub = fromB64u(V.gPub);
  const jwk = { kty: 'EC', crv: 'P-256', x: toB64u(pub.slice(1, 33)), y: toB64u(pub.slice(33)), d: V.dG };
  const algorithm = { name: 'ECDH', namedCurve: 'P-256' };
  return {
    privateKey: await crypto.subtle.importKey('jwk', jwk, algorithm, false, ['deriveBits']),
    publicKey: await crypto.subtle.importKey('jwk', { kty: 'EC', crv: 'P-256', x: jwk.x, y: jwk.y }, algorithm, true, []),
  };
}

describe('phone pairing', () => {
  it('derives the code, key and id both sides agree on', async () => {
    const result = await derivePairing(fromB64u(V.z), fromB64u(V.gPub), fromB64u(V.pPub));
    expect(result).toEqual({ code: V.code, pairing: { id: V.id, key: V.key } });
  });

  it('commits to the glasses key, then reveals it with the same result', async () => {
    const exchange = await PairingExchange.start(await vectorKeys());
    expect(await exchange.commit()).toBe(V.commit);
    const revealed = await exchange.reveal(V.pPub);
    expect(revealed).toEqual({ key: V.gPub, code: V.code, pairing: { id: V.id, key: V.key } });
  });

  it('gives a different code for a different phone key', async () => {
    const exchange = await PairingExchange.start(await vectorKeys());
    const other = await PairingExchange.start();
    const stranger = (await other.reveal(V.gPub)).key; // any other valid P-256 point
    expect((await exchange.reveal(stranger)).code).not.toBe(V.code);
  });

  it('makes fresh keys each time', async () => {
    const a = await PairingExchange.start();
    const b = await PairingExchange.start();
    expect(await a.commit()).not.toBe(await b.commit());
  });
});

describe('phone session handshake', () => {
  it('matches the vector', async () => {
    const sk = await sessionKey(V.key, V.gNonce, V.pNonce);
    expect(await labelMac(sk, 'phone')).toBe(V.phoneMac);
    expect(await labelMac(sk, 'glasses')).toBe(V.glassesMac);
    expect(await sdpMac(sk, 'offer', V.sdp)).toBe(V.offerMac);
    expect(await sdpMac(sk, 'answer', V.sdp)).toBe(V.answerMac);
  });

  it('proves the phone, then answers with our proof and checks the offer', async () => {
    const handshake = new Handshake({ id: V.id, key: V.key });
    expect(handshake.hello()).toEqual({ type: 'hello', id: V.id, nonce: handshake.nonce });
    expect(fromB64u(handshake.nonce)).toHaveLength(16);

    const sk = await sessionKey(V.key, handshake.nonce, V.pNonce);
    const proof = await handshake.answerChallenge(V.pNonce, await labelMac(sk, 'phone'));
    expect(proof).toBe(await labelMac(sk, 'glasses'));
    expect(await handshake.offerIsGenuine(V.sdp, await sdpMac(sk, 'offer', V.sdp))).toBe(true);
    expect(await handshake.offerIsGenuine(V.sdp + 'a=x\r\n', await sdpMac(sk, 'offer', V.sdp))).toBe(false);
    expect(await handshake.answerMac(V.sdp)).toBe(await sdpMac(sk, 'answer', V.sdp));
  });

  it('refuses a phone that does not hold the key', async () => {
    const handshake = new Handshake({ id: V.id, key: V.key });
    expect(await handshake.answerChallenge(V.pNonce, V.phoneMac)).toBeNull(); // made for another nonce
    expect(await handshake.offerIsGenuine(V.sdp, V.offerMac)).toBe(false);
  });
});

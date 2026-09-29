// Getting from "Phone" on the first screen to a WebRTC connection with the phone, through the
// relay (phoneSignal.ts): pair if needed (a code on both screens, Approve on the phone), prove
// each other (phoneTrust.ts), then take the phone's signed offer and send a signed answer. Once
// the DataChannel is open, connected() closes the relay: from then on the session doesn't need
// the server. (It still needs the phone online: Meta's app takes the glasses' link down when the
// phone loses internet; architecture/phone-mode.md, "What it can't survive".)
import { describeRelayClose, type FromRelay, type IceCandidate, type PhoneState, type RelayHandlers, type RelaySocket } from './phoneSignal';
import { forgetPairing, Handshake, loadPairing, PairingExchange, type PairingResult } from './phoneTrust';

export interface ConnectEvents {
  /** The phone's state as the relay reports it. */
  onPhone(state: PhoneState): void;
  /** The pairing code to show while the phone asks for approval; null once it's done. */
  onCode(code: string | null): void;
  /** The phone's offer, checked: set it and give back our answer's SDP. */
  onOffer(sdp: string): Promise<string>;
  onCandidate(candidate: IceCandidate): void;
  onPong(t: number): void;
  /** Setting up failed; the reason is for the ended screen. */
  onFailed(reason: string): void;
}

export class PhoneConnector {
  private readonly relay: RelaySocket;
  private exchange: Promise<PairingExchange> | null = null;
  private revealed: PairingResult | null = null;
  private handshake: Handshake | null = null;
  /** Pairing is tried once per relay (again after the phone forgot us). */
  private paired = false;
  /** Our candidates wait for the answer: the phone can't use them before it. */
  private answerSent = false;
  private readonly pendingCandidates: IceCandidate[] = [];
  private done = false;

  constructor(
    open: (handlers: RelayHandlers) => RelaySocket,
    private readonly events: ConnectEvents,
    /** Pair even if a pairing is remembered (Pair again on the ended screen). */
    private readonly pairAgain = false,
  ) {
    this.relay = open({
      onMessage: (message) => void this.onMessage(message).catch(() => this.fail("Couldn't check the phone's reply.")),
      onClose: (reason) => this.fail(describeRelayClose(reason)),
    });
  }

  /** The DataChannel is open: the relay has done its job. */
  connected(): void {
    this.done = true;
    this.relay.close();
  }

  sendCandidate(candidate: IceCandidate): void {
    if (this.done) return;
    if (this.answerSent) this.relay.send({ type: 'iceCandidate', ...candidate });
    else this.pendingCandidates.push(candidate);
  }

  ping(): void {
    this.relay.send({ type: 'ping', t: Date.now() });
  }

  close(): void {
    this.done = true;
    this.relay.close();
  }

  private fail(reason: string): void {
    if (this.done) return;
    this.done = true;
    this.relay.close();
    this.events.onCode(null);
    this.events.onFailed(reason);
  }

  private async onMessage(message: FromRelay): Promise<void> {
    if (this.done) return;
    switch (message.type) {
      case 'phoneStatus':
        this.events.onPhone(message.state);
        if (message.state === 'ready') await this.begin();
        break;

      case 'pairKey': {
        if (!this.exchange || this.revealed) return;
        const revealed = await (await this.exchange).reveal(message.key);
        this.revealed = revealed;
        this.relay.send({ type: 'pairReveal', key: revealed.key });
        this.events.onCode(revealed.code);
        break;
      }

      case 'paired':
        if (!this.revealed) return;
        PairingExchange.keep(this.revealed.pairing);
        this.events.onCode(null);
        this.hello();
        break;

      case 'pairFailed':
        this.fail("Pairing wasn't approved on the phone (Reject, a timeout, or the codes didn't match).");
        break;

      case 'authFailed':
        // The phone doesn't know these glasses (forgotten there, or paired with other glasses since).
        forgetPairing();
        if (this.paired) this.fail("The phone didn't accept these glasses. Pair again.");
        else await this.pair();
        break;

      case 'challenge': {
        const proof = await this.handshake?.answerChallenge(message.nonce, message.mac);
        if (!proof) {
          this.fail("That wasn't your phone: it couldn't prove the pairing. Pair again if you reset it.");
          return;
        }
        this.relay.send({ type: 'proof', mac: proof });
        break;
      }

      case 'rtcOffer': {
        if (!this.handshake || !(await this.handshake.offerIsGenuine(message.sdp, message.mac))) {
          this.fail("The video offer wasn't signed by your phone.");
          return;
        }
        const answer = await this.events.onOffer(message.sdp);
        this.relay.send({ type: 'rtcAnswer', sdp: answer, mac: await this.handshake.answerMac(answer) });
        this.answerSent = true;
        for (const candidate of this.pendingCandidates.splice(0)) this.relay.send({ type: 'iceCandidate', ...candidate });
        break;
      }

      case 'iceCandidate':
        this.events.onCandidate(message);
        break;

      case 'pong':
        this.events.onPong(message.t);
        break;
    }
  }

  /** The phone is reached: prove each other, or pair first. */
  private async begin(): Promise<void> {
    if (this.handshake || this.exchange) return;
    if (this.pairAgain) forgetPairing();
    if (loadPairing()) this.hello();
    else await this.pair();
  }

  private async pair(): Promise<void> {
    this.paired = true;
    this.handshake = null;
    this.exchange = PairingExchange.start();
    this.relay.send({ type: 'pairStart', commit: await (await this.exchange).commit() });
  }

  private hello(): void {
    const pairing = loadPairing();
    if (!pairing) {
      this.fail("These glasses couldn't keep the pairing (storage blocked?).");
      return;
    }
    this.handshake = new Handshake(pairing);
    this.relay.send(this.handshake.hello());
  }
}

// The WebRTC side of a phone session. The phone's companion app offers a video track (its screen)
// and a DataChannel "input"; the offer, the answer and the ICE candidates go through the relay
// (phoneConnect.ts). No STUN/TURN: the phone's own addresses are the candidates, and the glasses
// reach them over their own link to the phone ("live (local)"), so the session needs no internet.
import { mediaPath } from './mediaStats';
import { parsePhoneMessage, type FromPhone, type ToPhone } from './phoneProtocol';
import type { IceCandidate } from './phoneSignal';

export interface PhoneLinkEvents {
  onState(state: RTCPeerConnectionState): void;
  onChannel(open: boolean): void;
  onMessage(message: FromPhone): void;
}

export class PhoneLink {
  private readonly pc = new RTCPeerConnection({ iceServers: [] });
  private channel: RTCDataChannel | null = null;
  private remoteSet = false;
  private readonly pendingCandidates: IceCandidate[] = [];

  constructor(
    private readonly onLocalCandidate: (candidate: IceCandidate) => void,
    video: HTMLVideoElement,
    private readonly events: PhoneLinkEvents,
  ) {
    this.pc.ontrack = (event) => {
      if (event.track.kind === 'video') video.srcObject = event.streams[0] ?? new MediaStream([event.track]);
    };
    this.pc.onicecandidate = (event) => {
      if (event.candidate) {
        this.onLocalCandidate({
          candidate: event.candidate.candidate,
          sdpMid: event.candidate.sdpMid,
          sdpMLineIndex: event.candidate.sdpMLineIndex,
        });
      }
    };
    this.pc.onconnectionstatechange = () => this.events.onState(this.pc.connectionState);
    this.pc.ondatachannel = (event) => {
      if (event.channel.label !== 'input') return;
      const channel = event.channel;
      this.channel = channel;
      channel.onopen = () => this.events.onChannel(true);
      channel.onclose = () => this.events.onChannel(false);
      channel.onmessage = (e) => {
        const message = typeof e.data === 'string' ? parsePhoneMessage(e.data) : null;
        if (message) this.events.onMessage(message);
      };
      if (channel.readyState === 'open') this.events.onChannel(true);
    };
  }

  /** Sets the phone's (checked) offer and returns our answer's SDP. */
  async handleOffer(sdp: string): Promise<string> {
    await this.pc.setRemoteDescription({ type: 'offer', sdp });
    this.remoteSet = true;
    for (const candidate of this.pendingCandidates.splice(0)) await this.addCandidate(candidate);
    const answer = await this.pc.createAnswer();
    await this.pc.setLocalDescription(answer);
    return this.pc.localDescription!.sdp;
  }

  /** The phone's trickled candidates; held until its offer is in place. */
  async addCandidate(candidate: IceCandidate): Promise<void> {
    if (!this.remoteSet) {
      this.pendingCandidates.push(candidate);
      return;
    }
    try {
      await this.pc.addIceCandidate(candidate);
    } catch {
      // A candidate this browser can't use (e.g. another address family): the others may do.
    }
  }

  /** Sends on the DataChannel; false while it isn't open (the input is dropped, not queued). */
  send(message: ToPhone): boolean {
    if (this.channel?.readyState !== 'open') return false;
    this.channel.send(JSON.stringify(message));
    return true;
  }

  /**
   * The phone's address on the chosen candidate pair, and whether it's a private one ("local"),
   * once known. The address is shown too: a mobile network's private 10.x address counts as
   * local but goes away with the phone's data.
   */
  async path(): Promise<{ kind: 'local' | 'remote'; address: string } | null> {
    const report = await this.pc.getStats();
    let pairId: string | null = null;
    const byId = new Map<string, Record<string, unknown>>();
    report.forEach((s: Record<string, unknown>) => {
      byId.set(s.id as string, s);
      if (s.type === 'transport' && typeof s.selectedCandidatePairId === 'string') pairId = s.selectedCandidatePairId;
    });
    const pair = pairId ? byId.get(pairId) : null;
    const remote = pair ? byId.get(pair.remoteCandidateId as string) : null;
    const address = (remote?.address ?? remote?.ip) as string | undefined;
    const kind = mediaPath(address);
    return kind && address ? { kind, address } : null;
  }

  close(): void {
    this.channel?.close();
    this.pc.close();
  }
}

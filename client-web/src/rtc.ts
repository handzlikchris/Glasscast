// Receive-only WebRTC video from the PC. The server sends the offer; we answer.
// No STUN/TURN: the offer already carries the router's public address and the
// forwarded media port, and our checks go straight there.
import { receiverStats, type InboundSnapshot, type ReceiverStats, type ShownFrame } from './mediaStats';
import type { ClientMessage } from './protocol';

export interface VideoStats {
  fps: number | null;
  codec: string | null;
  rttMs: number | null;
  receiver: ReceiverStats | null;
}

export class VideoReceiver {
  private readonly pc = new RTCPeerConnection({ iceServers: [] });
  private lastInbound: InboundSnapshot | null = null;

  constructor(
    private readonly send: (message: ClientMessage) => void,
    video: HTMLVideoElement,
    onState: (state: RTCPeerConnectionState) => void,
  ) {
    this.pc.ontrack = (event) => {
      video.srcObject = event.streams[0] ?? new MediaStream([event.track]);
    };
    this.pc.onicecandidate = (event) => {
      if (event.candidate) {
        this.send({
          type: 'iceCandidate',
          candidate: event.candidate.candidate,
          sdpMid: event.candidate.sdpMid,
          sdpMLineIndex: event.candidate.sdpMLineIndex,
        });
      }
    };
    this.pc.onconnectionstatechange = () => onState(this.pc.connectionState);
  }

  async handleOffer(sdp: string): Promise<void> {
    await this.pc.setRemoteDescription({ type: 'offer', sdp });
    await this.pc.setLocalDescription(await this.pc.createAnswer());
    this.send({ type: 'rtcAnswer', sdp: this.pc.localDescription!.sdp });
  }

  async stats(): Promise<VideoStats> {
    const report = await this.pc.getStats();
    const byId = new Map<string, Record<string, unknown>>();
    let inbound: Record<string, unknown> | null = null;
    let pairId: string | null = null;

    report.forEach((s: Record<string, unknown>) => {
      byId.set(s.id as string, s);
      if (s.type === 'inbound-rtp' && s.kind === 'video') inbound = s;
      if (s.type === 'transport' && typeof s.selectedCandidatePairId === 'string') pairId = s.selectedCandidatePairId;
    });

    const result: VideoStats = { fps: null, codec: null, rttMs: null, receiver: null };
    const video = inbound as Record<string, unknown> | null;
    if (video) {
      result.fps = typeof video.framesPerSecond === 'number' ? video.framesPerSecond : null;
      const codec = byId.get(video.codecId as string);
      result.codec = codec ? String(codec.mimeType).replace('video/', '') : null;

      const n = (key: string) => Number(video[key] ?? 0);
      const snapshot: InboundSnapshot = {
        at: performance.now(),
        jitterBufferDelay: n('jitterBufferDelay'),
        jitterBufferEmittedCount: n('jitterBufferEmittedCount'),
        framesDecoded: n('framesDecoded'),
        totalDecodeTime: n('totalDecodeTime'),
        bytesReceived: n('bytesReceived'),
        packetsLost: n('packetsLost'),
        nackCount: n('nackCount'),
        pliCount: n('pliCount'),
        freezeCount: n('freezeCount'),
        framesDropped: n('framesDropped'),
        keyFramesDecoded: n('keyFramesDecoded'),
      };
      result.receiver = receiverStats(this.lastInbound, snapshot);
      this.lastInbound = snapshot;
    }

    const pair = pairId ? byId.get(pairId) : null;
    if (pair && typeof pair.currentRoundTripTime === 'number') {
      result.rttMs = pair.currentRoundTripTime * 1000;
    }
    return result;
  }

  close(): void {
    this.pc.close();
  }
}

/**
 * Calls back for every frame the video element presents, with the RTP timestamp it arrived with
 * (requestVideoFrameCallback). Times are epoch ms on this device's clock. Returns a stop function;
 * does nothing where the browser lacks the API.
 */
export function watchFrames(video: HTMLVideoElement, onFrame: (frame: ShownFrame) => void): () => void {
  if (typeof video.requestVideoFrameCallback !== 'function') return () => {};
  let handle = 0;
  let stopped = false;
  const tick = (_now: number, meta: VideoFrameCallbackMetadata) => {
    if (stopped) return;
    if (typeof meta.rtpTimestamp === 'number') {
      onFrame({
        rtp: meta.rtpTimestamp,
        receivedAt: typeof meta.receiveTime === 'number' ? performance.timeOrigin + meta.receiveTime : null,
        shownAt: performance.timeOrigin + meta.expectedDisplayTime,
      });
    }
    handle = video.requestVideoFrameCallback(tick);
  };
  handle = video.requestVideoFrameCallback(tick);
  return () => {
    stopped = true;
    video.cancelVideoFrameCallback(handle);
  };
}

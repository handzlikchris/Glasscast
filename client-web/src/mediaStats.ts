// Where the video latency goes, for the stats panel. The PC lists every frame it sends with its
// RTP timestamp and capture time (mediaStats); the video element reports the RTP timestamp of
// each frame it shows. Matching the two, with the PC's clock offset from ping/pong, gives
// capture-to-display latency per frame. Pure logic: times are epoch milliseconds.
import type { PcMediaStats, SentFrame, StatsReport } from './protocol';

/** Ping/pong samples kept for the clock offset (one every 2 s: the last 20 s). */
const CLOCK_SAMPLES = 10;
/** Frames waiting for their other half are dropped after this long. */
const MATCH_WINDOW_MS = 5_000;
/** Averages cover the last 2 s, maxima the last 10 s (long enough to read after a screen change). */
export const AVG_WINDOW_MS = 2_000;
export const MAX_WINDOW_MS = 10_000;

/** Offset from this device's clock to the PC's: pcTime ≈ localTime + offset. */
export class ClockSync {
  private samples: { rtt: number; offset: number }[] = [];

  /** A ping sent at `sentAt` and answered at `receivedAt` (local), stamped `pcTime` by the PC. */
  add(sentAt: number, receivedAt: number, pcTime: number): void {
    const rtt = receivedAt - sentAt;
    if (rtt < 0) return;
    this.samples.push({ rtt, offset: pcTime - (sentAt + rtt / 2) });
    if (this.samples.length > CLOCK_SAMPLES) this.samples.shift();
  }

  /** From the quickest round trip: the PC stamped it within ±rtt/2 of our midpoint. */
  get best(): { offset: number; errorMs: number } | null {
    if (this.samples.length === 0) return null;
    const s = this.samples.reduce((a, b) => (b.rtt < a.rtt ? b : a));
    return { offset: s.offset, errorMs: s.rtt / 2 };
  }
}

/** A frame the video element showed. `receivedAt` is when its last packet arrived, if the browser says. */
export interface ShownFrame {
  rtp: number;
  receivedAt: number | null;
  shownAt: number;
}

interface Matched {
  capturedAt: number; // PC clock
  receivedAt: number | null;
  shownAt: number;
  bytes: number;
}

export interface Span {
  avg: number;
  max: number;
}

export interface LatencySummary {
  /** Frames shown in the averaging window. */
  frames: number;
  /** PC capture start → shown here. */
  total: Span;
  /** PC capture start → last packet here (capture, encode, send, network). */
  arrival: Span | null;
  /** Last packet here → shown (jitter buffer, decode, render). */
  playout: Span | null;
  /** Size of the slowest frame in the max window, KB. */
  slowestKb: number;
  clockErrorMs: number;
}

export class FrameLatency {
  private readonly sent = new Map<number, { frame: SentFrame; at: number }>();
  private readonly shown = new Map<number, ShownFrame>();
  private matched: Matched[] = [];
  /** Frames the video element reported with an RTP timestamp (0 means the browser can't). */
  shownCount = 0;

  constructor(private readonly clock: ClockSync) {}

  addSent(frames: readonly SentFrame[], now: number): void {
    for (const frame of frames) {
      const shown = this.shown.get(frame.rtp);
      if (shown) {
        this.shown.delete(frame.rtp);
        this.match(frame, shown);
      } else {
        this.sent.set(frame.rtp, { frame, at: now });
      }
    }
    this.prune(now);
  }

  addShown(frame: ShownFrame): void {
    this.shownCount++;
    const sent = this.sent.get(frame.rtp);
    if (sent) {
      this.sent.delete(frame.rtp);
      this.match(sent.frame, frame);
    } else {
      this.shown.set(frame.rtp, frame);
    }
  }

  summary(now: number): LatencySummary | null {
    const clock = this.clock.best;
    if (!clock) return null;
    const recent = this.matched.filter((m) => m.shownAt >= now - MAX_WINDOW_MS);
    if (recent.length === 0) return null;

    // PC capture time on our clock.
    const captured = (m: Matched) => m.capturedAt - clock.offset;
    const span = (values: { at: number; v: number }[]): Span | null => {
      if (values.length === 0) return null;
      const avgOf = values.filter((x) => x.at >= now - AVG_WINDOW_MS);
      const pool = avgOf.length > 0 ? avgOf : values.slice(-1);
      return {
        avg: pool.reduce((sum, x) => sum + x.v, 0) / pool.length,
        max: Math.max(...values.map((x) => x.v)),
      };
    };

    const total = span(recent.map((m) => ({ at: m.shownAt, v: m.shownAt - captured(m) })))!;
    const withArrival = recent.filter((m) => m.receivedAt !== null);
    const slowest = recent.reduce((a, b) => (b.shownAt - captured(b) > a.shownAt - captured(a) ? b : a));
    return {
      frames: recent.filter((m) => m.shownAt >= now - AVG_WINDOW_MS).length,
      total,
      arrival: span(withArrival.map((m) => ({ at: m.shownAt, v: m.receivedAt! - captured(m) }))),
      playout: span(withArrival.map((m) => ({ at: m.shownAt, v: m.shownAt - m.receivedAt! }))),
      slowestKb: slowest.bytes / 1024,
      clockErrorMs: clock.errorMs,
    };
  }

  private match(frame: SentFrame, shown: ShownFrame): void {
    this.matched.push({ capturedAt: frame.capturedAt, receivedAt: shown.receivedAt, shownAt: shown.shownAt, bytes: frame.bytes });
  }

  private prune(now: number): void {
    for (const [rtp, s] of this.sent) if (s.at < now - MATCH_WINDOW_MS) this.sent.delete(rtp);
    for (const [rtp, s] of this.shown) if (s.shownAt < now - MATCH_WINDOW_MS) this.shown.delete(rtp);
    this.matched = this.matched.filter((m) => m.shownAt >= now - MAX_WINDOW_MS);
  }
}

/** Cumulative inbound-rtp counters from getStats() (times in seconds, as the browser gives them). */
export interface InboundSnapshot {
  at: number;
  jitterBufferDelay: number;
  jitterBufferEmittedCount: number;
  framesDecoded: number;
  totalDecodeTime: number;
  bytesReceived: number;
  packetsLost: number;
  nackCount: number;
  pliCount: number;
  freezeCount: number;
  framesDropped: number;
  keyFramesDecoded: number;
}

/** What the receiver did since the previous snapshot (rates, per-frame times) plus session totals. */
export interface ReceiverStats {
  jitterBufferMs: number | null;
  decodeMs: number | null;
  kbps: number | null;
  lost: number;
  lostTotal: number;
  nacks: number;
  plis: number;
  freezes: number;
  dropped: number;
  keyframes: number;
}

export function receiverStats(prev: InboundSnapshot | null, cur: InboundSnapshot): ReceiverStats {
  const perFrameMs = (time: keyof InboundSnapshot, count: keyof InboundSnapshot) => {
    if (!prev || cur[count] <= prev[count]) return null;
    return ((cur[time] - prev[time]) / (cur[count] - prev[count])) * 1000;
  };
  const seconds = prev ? (cur.at - prev.at) / 1000 : 0;
  return {
    jitterBufferMs: perFrameMs('jitterBufferDelay', 'jitterBufferEmittedCount'),
    decodeMs: perFrameMs('totalDecodeTime', 'framesDecoded'),
    kbps: prev && seconds > 0 ? ((cur.bytesReceived - prev.bytesReceived) * 8) / seconds / 1000 : null,
    lost: prev ? Math.max(0, cur.packetsLost - prev.packetsLost) : 0,
    lostTotal: cur.packetsLost,
    nacks: cur.nackCount,
    plis: cur.pliCount,
    freezes: cur.freezeCount,
    dropped: cur.framesDropped,
    keyframes: cur.keyFramesDecoded,
  };
}

/** Cumulative inbound-rtp counters of the audio track (samples per channel, times in seconds). */
export interface AudioSnapshot {
  at: number;
  bytesReceived: number;
  packetsLost: number;
  /** Samples the decoder made up (lost or late packets); the silent ones are its DTX gaps. */
  concealedSamples: number;
  silentConcealedSamples: number;
  jitterBufferDelay: number;
  jitterBufferEmittedCount: number;
}

/** The PC's sound as received since the previous snapshot. */
export interface AudioStats {
  /** Opus payload, kbit/s: comparable with the PC's audioKbps. */
  kbps: number | null;
  lost: number;
  /** Sound made up by the decoder, ms in the interval. Not counting silence the PC didn't send. */
  concealedMs: number | null;
  /** Average wait in the audio jitter buffer. */
  bufferMs: number | null;
}

/** Opus runs at 48 kHz: samples per millisecond. */
const OPUS_SAMPLES_PER_MS = 48;

export function audioStats(prev: AudioSnapshot | null, cur: AudioSnapshot): AudioStats {
  if (!prev) return { kbps: null, lost: 0, concealedMs: null, bufferMs: null };
  const seconds = (cur.at - prev.at) / 1000;
  const emitted = cur.jitterBufferEmittedCount - prev.jitterBufferEmittedCount;
  const concealed =
    cur.concealedSamples - prev.concealedSamples - (cur.silentConcealedSamples - prev.silentConcealedSamples);
  return {
    kbps: seconds > 0 ? ((cur.bytesReceived - prev.bytesReceived) * 8) / seconds / 1000 : null,
    lost: Math.max(0, cur.packetsLost - prev.packetsLost),
    concealedMs: Math.max(0, concealed) / OPUS_SAMPLES_PER_MS,
    bufferMs: emitted > 0 ? ((cur.jitterBufferDelay - prev.jitterBufferDelay) / emitted) * 1000 : null,
  };
}

/**
 * How this device reaches the network, to tell a slow hop from a slow internet (on the glasses:
 * Wi-Fi, or relayed by the phone over Bluetooth?). Types as number codes, see NETWORK_CODES.
 */
export interface NetworkInfo {
  /** navigator.connection.type (Network Information API). */
  type: number | null;
  /** networkType of the local ICE candidate the video arrives on. */
  iceType: number | null;
  /** navigator.connection.downlink: the browser's own bandwidth estimate. */
  downlinkMbps: number | null;
  /** Round trip of the video's UDP path (ICE consent checks). */
  rttMs: number | null;
}

/** Codes for network types in the stats log (only numbers go there). 0 = anything else. */
export const NETWORK_CODES: Record<string, number> = {
  wifi: 1,
  cellular: 2,
  bluetooth: 3,
  ethernet: 4,
  vpn: 5,
  wimax: 6,
  other: 7,
  none: 8,
};

export const networkCode = (name: unknown): number | null =>
  typeof name === 'string' ? (NETWORK_CODES[name] ?? 0) : null;

const networkName = (code: number | null) =>
  code === null ? '–' : (Object.keys(NETWORK_CODES).find((k) => NETWORK_CODES[k] === code) ?? 'unknown');

/**
 * Which way the video comes: 'local' when the PC's end of the chosen ICE pair is a private
 * address (the PC offers its LAN address first to glasses at home), 'remote' through the
 * internet, null before the pair is known.
 */
export function mediaPath(pcAddress: unknown): 'local' | 'remote' | null {
  if (typeof pcAddress !== 'string' || pcAddress === '') return null;
  const v4 = pcAddress.match(/^(\d+)\.(\d+)\.\d+\.\d+$/);
  if (v4) {
    const [a, b] = [Number(v4[1]), Number(v4[2])];
    return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || a === 127 ? 'local' : 'remote';
  }
  return /^(fc|fd|fe80|::1$)/i.test(pcAddress) ? 'local' : 'remote';
}

/** PC stats messages kept for the panel: about the last 10 s. */
export const PC_STATS_KEPT = MAX_WINDOW_MS / 1000;

const ms = (v: number | null | undefined) => (v === null || v === undefined ? '–' : Math.round(v).toString());

/** The stats panel's lines. `pc` is the recent mediaStats messages, oldest first. */
export function statsLines(
  latency: LatencySummary | null,
  rx: ReceiverStats | null,
  pc: readonly PcMediaStats[],
  net: NetworkInfo | null = null,
  audio: AudioStats | null = null,
): string[] {
  const lines: string[] = [];
  if (latency) {
    lines.push(
      `e2e ${ms(latency.total.avg)} ms · max ${ms(latency.total.max)} (${ms(latency.slowestKb)} KB) · clock ±${ms(latency.clockErrorMs)}`,
    );
    if (latency.arrival && latency.playout) {
      lines.push(
        `PC→here ${ms(latency.arrival.avg)} (max ${ms(latency.arrival.max)}) · buffer+show ${ms(latency.playout.avg)} (max ${ms(latency.playout.max)})`,
      );
    }
  } else {
    lines.push('e2e – (no frame timings yet)');
  }

  if (rx) {
    const mbps = rx.kbps === null ? '–' : (rx.kbps / 1000).toFixed(1);
    lines.push(`jitter buf ${ms(rx.jitterBufferMs)} ms · decode ${ms(rx.decodeMs)} ms · ${mbps} Mbps in`);
    lines.push(`lost ${rx.lost} (${rx.lostTotal}) · nack ${rx.nacks} · pli ${rx.plis} · freezes ${rx.freezes} · dropped ${rx.dropped}`);
  }

  const lastPc = pc.length > 0 ? pc[pc.length - 1] : null;
  if (audio || lastPc?.audioOn) {
    lines.push(
      audio
        ? `audio ${ms(audio.kbps)} kbps · lost ${audio.lost} · concealed ${ms(audio.concealedMs)} ms · buffer ${ms(audio.bufferMs)} ms · PC ${ms(lastPc?.audioKbps)} kbps`
        : `audio – (PC ${ms(lastPc?.audioKbps)} kbps)`,
    );
  }

  if (net) {
    const down = net.downlinkMbps === null ? '–' : net.downlinkMbps.toFixed(1);
    lines.push(`net ${networkName(net.type)} · ICE ${networkName(net.iceType)} · ${down} Mbps est · rtt ${ms(net.rttMs)} ms`);
  }

  if (pc.length > 0) {
    const recent = pc.slice(-2);
    const avg = (pick: (s: PcMediaStats) => number) => recent.reduce((sum, s) => sum + pick(s), 0) / recent.length;
    const max = (pick: (s: PcMediaStats) => number) => Math.max(...pc.map(pick));
    const recentFrames = recent.flatMap((s) => s.frames);
    const frameKb = recentFrames.length > 0 ? recentFrames.reduce((sum, f) => sum + f.bytes, 0) / recentFrames.length / 1024 : null;
    const maxKb = Math.max(0, ...pc.flatMap((s) => s.frames.map((f) => f.bytes))) / 1024;
    const keyframes = pc.reduce((sum, s) => sum + s.keyframes, 0);
    const asked = pc.reduce((sum, s) => sum + s.keyframeRequests, 0);
    const nacked = pc.reduce((sum, s) => sum + s.nacked, 0);
    const resent = pc.reduce((sum, s) => sum + s.resent, 0);
    const unreadable = pc.reduce((sum, s) => sum + s.rtcpUnreadable, 0);
    lines.push(
      `PC capture ${ms(avg((s) => s.captureMs))} (max ${ms(max((s) => s.captureMaxMs))}) · encode ${ms(avg((s) => s.encodeMs))} (max ${ms(max((s) => s.encodeMaxMs))}) · send ${ms(avg((s) => s.sendMs))} (max ${ms(max((s) => s.sendMaxMs))}) ms`,
    );
    lines.push(
      `PC frame ${ms(frameKb)} KB (max ${ms(maxKb)}) · ${ms(pc[pc.length - 1].fps)} fps · ${ms(pc[pc.length - 1].kbps)} kbps · keyframes ${keyframes} (asked ${asked})`,
    );
    const last = pc[pc.length - 1];
    lines.push(
      `PC target ${ms(last.targetKbps)} kbps · REMB ${ms(last.rembKbps)} · loss ${ms(last.lossPct)}% · resent ${resent} of ${nacked}` +
        (unreadable > 0 ? ` · RTCP unreadable ${unreadable}` : ''),
    );
    if (last.linkTestKbps > 0) lines.unshift(`LINK TEST ${last.linkTestKbps} kbps`);
  }
  return lines;
}

/** What the glasses send to the PC's stats log each second: the panel's figures as numbers. */
export function statsReport(
  latency: LatencySummary | null,
  rx: ReceiverStats | null,
  fps: number | null,
  framesShown: number,
  net: NetworkInfo | null = null,
  audio: AudioStats | null = null,
): StatsReport {
  const r = (v: number | null | undefined) => (v === null || v === undefined ? null : Math.round(v * 10) / 10);
  return {
    e2eMs: r(latency?.total.avg),
    e2eMaxMs: r(latency?.total.max),
    arrivalMs: r(latency?.arrival?.avg),
    arrivalMaxMs: r(latency?.arrival?.max),
    playoutMs: r(latency?.playout?.avg),
    playoutMaxMs: r(latency?.playout?.max),
    slowestKb: r(latency?.slowestKb),
    clockErrorMs: r(latency?.clockErrorMs),
    framesShown,
    fps: r(fps),
    jitterBufferMs: r(rx?.jitterBufferMs),
    decodeMs: r(rx?.decodeMs),
    kbps: r(rx?.kbps),
    lost: rx?.lost ?? null,
    lostTotal: rx?.lostTotal ?? null,
    nacks: rx?.nacks ?? null,
    plis: rx?.plis ?? null,
    freezes: rx?.freezes ?? null,
    dropped: rx?.dropped ?? null,
    keyframes: rx?.keyframes ?? null,
    netType: net?.type ?? null,
    iceNetType: net?.iceType ?? null,
    downlinkMbps: r(net?.downlinkMbps),
    rttMs: r(net?.rttMs),
    audioKbps: r(audio?.kbps),
    audioLost: audio?.lost ?? null,
    audioConcealedMs: r(audio?.concealedMs),
    audioBufferMs: r(audio?.bufferMs),
  };
}

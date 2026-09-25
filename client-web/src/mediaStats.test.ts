import { describe, expect, it } from 'vitest';
import { ClockSync, FrameLatency, receiverStats, statsLines, type InboundSnapshot } from './mediaStats';

describe('ClockSync', () => {
  it('uses the ping with the quickest round trip', () => {
    const clock = new ClockSync();
    expect(clock.best).toBeNull();
    // PC clock 500 ms ahead. Slow ping: its stamp was late, so its offset is off.
    clock.add(1000, 1200, 1000 + 500 + 150);
    clock.add(3000, 3020, 3000 + 500 + 10);
    expect(clock.best).toEqual({ offset: 500, errorMs: 10 });
  });
});

describe('FrameLatency', () => {
  const clockAhead = (ms: number) => {
    const clock = new ClockSync();
    clock.add(0, 0, ms);
    return clock;
  };

  it('matches frames by RTP timestamp in either order and splits the latency', () => {
    const latency = new FrameLatency(clockAhead(500));
    // Frame 1: stats arrive before the frame is shown. PC captured at 10_500 = our 10_000.
    latency.addSent([{ rtp: 4500, capturedAt: 10_500, bytes: 20_480 }], 10_050);
    latency.addShown({ rtp: 4500, receivedAt: 10_040, shownAt: 10_100 });
    // Frame 2: shown before its stats arrive; slower and bigger.
    latency.addShown({ rtp: 9000, receivedAt: 10_200, shownAt: 10_300 });
    latency.addSent([{ rtp: 9000, capturedAt: 10_550, bytes: 102_400 }], 10_900);

    expect(latency.summary(11_000)).toEqual({
      frames: 2,
      total: { avg: 175, max: 250 },
      arrival: { avg: 95, max: 150 },
      playout: { avg: 80, max: 100 },
      slowestKb: 100,
      clockErrorMs: 0,
    });
  });

  it('averages recent frames but keeps the worst for longer', () => {
    const latency = new FrameLatency(clockAhead(0));
    latency.addSent([{ rtp: 1, capturedAt: 0, bytes: 1024 }], 0);
    latency.addShown({ rtp: 1, receivedAt: null, shownAt: 400 });
    latency.addSent([{ rtp: 2, capturedAt: 5_000, bytes: 1024 }], 5_000);
    latency.addShown({ rtp: 2, receivedAt: null, shownAt: 5_100 });

    const summary = latency.summary(6_000)!;
    expect(summary.total).toEqual({ avg: 100, max: 400 });
    expect(summary.arrival).toBeNull();
    expect(latency.summary(10_500)!.total.max).toBe(100);
  });

  it('reports nothing without a clock or matched frames', () => {
    const latency = new FrameLatency(new ClockSync());
    latency.addSent([{ rtp: 1, capturedAt: 0, bytes: 1 }], 0);
    latency.addShown({ rtp: 1, receivedAt: 1, shownAt: 2 });
    expect(latency.summary(10)).toBeNull();
    expect(new FrameLatency(clockAhead(0)).summary(10)).toBeNull();
  });
});

describe('receiverStats', () => {
  const snapshot = (over: Partial<InboundSnapshot>): InboundSnapshot => ({
    at: 0,
    jitterBufferDelay: 0,
    jitterBufferEmittedCount: 0,
    framesDecoded: 0,
    totalDecodeTime: 0,
    bytesReceived: 0,
    packetsLost: 0,
    nackCount: 0,
    pliCount: 0,
    freezeCount: 0,
    framesDropped: 0,
    keyFramesDecoded: 0,
    ...over,
  });

  it('turns cumulative counters into per-frame times and rates', () => {
    const prev = snapshot({ at: 1000, jitterBufferDelay: 1, jitterBufferEmittedCount: 10, framesDecoded: 10, totalDecodeTime: 0.1, bytesReceived: 100_000, packetsLost: 2 });
    const cur = snapshot({ at: 2000, jitterBufferDelay: 2, jitterBufferEmittedCount: 30, framesDecoded: 30, totalDecodeTime: 0.3, bytesReceived: 400_000, packetsLost: 5, pliCount: 1 });
    const stats = receiverStats(prev, cur);
    expect(stats.decodeMs).toBeCloseTo(10);
    expect(stats).toMatchObject({
      jitterBufferMs: 50,
      kbps: 2400,
      lost: 3,
      lostTotal: 5,
      plis: 1,
    });
  });

  it('has no rates on the first snapshot or when no frames came out', () => {
    const first = receiverStats(null, snapshot({ at: 1000, packetsLost: 4 }));
    expect(first).toMatchObject({ jitterBufferMs: null, decodeMs: null, kbps: null, lost: 0, lostTotal: 4 });
    expect(receiverStats(snapshot({}), snapshot({ at: 1000 })).jitterBufferMs).toBeNull();
  });
});

describe('statsLines', () => {
  it('shows latency, receiver and PC figures', () => {
    const lines = statsLines(
      {
        frames: 20,
        total: { avg: 120.4, max: 310 },
        arrival: { avg: 40, max: 200 },
        playout: { avg: 80, max: 110 },
        slowestKb: 95.6,
        clockErrorMs: 12,
      },
      { jitterBufferMs: 45, decodeMs: 8, kbps: 2400, lost: 0, lostTotal: 3, nacks: 1, plis: 2, freezes: 0, dropped: 1, keyframes: 9 },
      [
        { fps: 20, captureMs: 10, captureMaxMs: 30, encodeMs: 6, encodeMaxMs: 25, kbps: 2500, keyframes: 1, frames: [{ rtp: 1, capturedAt: 0, bytes: 102_400 }] },
        { fps: 20, captureMs: 12, captureMaxMs: 14, encodeMs: 8, encodeMaxMs: 9, kbps: 2400, keyframes: 0, frames: [{ rtp: 2, capturedAt: 0, bytes: 10_240 }] },
      ],
    );
    expect(lines).toEqual([
      'e2e 120 ms · max 310 (96 KB) · clock ±12',
      'PC→here 40 (max 200) · buffer+show 80 (max 110)',
      'jitter buf 45 ms · decode 8 ms · 2.4 Mbps in',
      'lost 0 (3) · nack 1 · pli 2 · freezes 0 · dropped 1',
      'PC capture 11 (max 30) · encode 7 (max 25) ms',
      'PC frame 55 KB (max 100) · 20 fps · 2400 kbps · keyframes 1',
    ]);
  });

  it('says when there are no frame timings yet', () => {
    expect(statsLines(null, null, [])).toEqual(['e2e – (no frame timings yet)']);
  });
});

// Plays the PC's sound through Web Audio instead of straight from the <audio> element.
//
// On the glasses the sound arrived and was decoded (the jitter buffer drained) but nothing was
// heard. Android can route a media element playing a WebRTC stream like a call rather than as
// media; Web Audio output goes out as media. The element stays, muted: Chrome only pulls a
// remote WebRTC track into Web Audio while a media element is playing it.
//
// Also a short beep when ♪ turns on, straight to the speakers: it tells "the glasses can play
// sound at all" apart from "the stream doesn't come through". DOM glue, covered by the e2e run.

/** How long to wait for AudioContext.resume(): without a user gesture it never settles. */
const RESUME_WAIT_MS = 300;

export type OutputState = AudioContextState | 'none';

export class AudioOutput {
  private ctx: AudioContext | null = null;
  private gain: GainNode | null = null;
  private source: MediaStreamAudioSourceNode | null = null;

  constructor(private readonly element: HTMLAudioElement) {}

  get state(): OutputState {
    return this.ctx?.state ?? 'none';
  }

  /** The PC's audio track arrived. */
  attach(stream: MediaStream): void {
    this.element.srcObject = stream;
    this.element.muted = true;
    const { ctx, gain } = this.context();
    this.source?.disconnect();
    this.source = ctx.createMediaStreamSource(stream);
    this.source.connect(gain);
  }

  /**
   * Sound on or off. Resolves false while the browser won't start sound without a gesture
   * (then call again from the next pinch or swipe).
   */
  async play(on: boolean): Promise<boolean> {
    const { ctx, gain } = this.context();
    gain.gain.value = on ? 1 : 0;
    if (this.element.srcObject) void this.element.play().catch(() => {}); // muted: always allowed
    if (!on) return true;
    if (ctx.state !== 'running') {
      await Promise.race([ctx.resume().catch(() => {}), new Promise((r) => setTimeout(r, RESUME_WAIT_MS))]);
    }
    return ctx.state === 'running';
  }

  /** A quarter-second 880 Hz beep, straight to the speakers. */
  beep(): void {
    const { ctx } = this.context();
    const t = ctx.currentTime;
    const osc = ctx.createOscillator();
    const envelope = ctx.createGain();
    osc.frequency.value = 880;
    envelope.gain.setValueAtTime(0.0001, t);
    envelope.gain.exponentialRampToValueAtTime(0.3, t + 0.02);
    envelope.gain.exponentialRampToValueAtTime(0.0001, t + 0.25);
    osc.connect(envelope).connect(ctx.destination);
    osc.start(t);
    osc.stop(t + 0.3);
  }

  close(): void {
    this.source?.disconnect();
    void this.ctx?.close().catch(() => {});
    this.ctx = null;
  }

  private context(): { ctx: AudioContext; gain: GainNode } {
    if (!this.ctx || !this.gain) {
      this.ctx = new AudioContext({ latencyHint: 'interactive' });
      this.gain = this.ctx.createGain();
      this.gain.connect(this.ctx.destination);
    }
    return { ctx: this.ctx, gain: this.gain };
  }
}

// The PC's sound on the glasses: the ♪ setting (remembered on this device, like brightness), the
// SDP detail that makes the browser decode it in stereo, and the status bar's bandwidth label.
// Only the on/off setting is stored: the session token never goes near storage (see connection.ts).

const STORAGE_KEY = 'glasses.audio';

/** On unless it was turned off on this device. */
export function parseAudioOn(stored: string | null): boolean {
  return stored !== 'off';
}

export function loadAudioOn(): boolean {
  try {
    return parseAudioOn(localStorage.getItem(STORAGE_KEY));
  } catch {
    return true;
  }
}

export function saveAudioOn(on: boolean): void {
  try {
    localStorage.setItem(STORAGE_KEY, on ? 'on' : 'off');
  } catch {
    // No storage (private mode, blocked): it just won't be remembered.
  }
}

/**
 * Chrome decodes Opus in mono unless its own description says `stereo=1`, and it doesn't put
 * that in the answer it creates, whatever the offer says. Adds it to our answer's Opus line
 * before the answer is applied and sent.
 */
export function withStereoOpus(sdp: string): string {
  const pt = sdp.match(/^a=rtpmap:(\d+) opus\/48000\/2\r?$/im)?.[1];
  if (!pt) return sdp;
  return sdp.replace(new RegExp(`^(a=fmtp:${pt} )(.*?)(\\r?)$`, 'm'), (line, head: string, params: string, cr: string) =>
    /(^|;)\s*stereo=/.test(params) ? line : `${head}${params};stereo=1${cr}`,
  );
}

/** What the audio is doing, for the status bar. */
export type AudioState = 'none' | 'off' | 'blocked' | 'on';

/**
 * The status bar's bandwidth: video and audio as received, kbit/s of payload (headers not
 * counted, like the PC's own figures). `none` when the PC offers no sound.
 */
export function bandwidthLabel(videoKbps: number | null, audioKbps: number | null, audio: AudioState): string {
  const n = (v: number | null) => (v === null ? '–' : Math.round(v).toString());
  const video = `V ${n(videoKbps)}`;
  switch (audio) {
    case 'none':
      return `${video} kbps`;
    case 'off':
      return `${video} kbps · A off`;
    default:
      return `${video} · A ${n(audioKbps)} kbps`;
  }
}

/** The ♪ button's label: on, off, or waiting for a tap before the browser lets it play. */
export function audioButtonLabel(audio: AudioState): string {
  return audio === 'off' ? '♪ off' : audio === 'blocked' ? '♪ tap' : '♪';
}

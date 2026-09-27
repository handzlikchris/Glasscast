import { describe, expect, it } from 'vitest';
import { audioButtonLabel, bandwidthLabel, loadAudioOn, parseAudioOn, withStereoOpus } from './audio';

describe('audio setting', () => {
  it('is on unless turned off on this device', () => {
    expect(parseAudioOn(null)).toBe(true);
    expect(parseAudioOn('on')).toBe(true);
    expect(parseAudioOn('off')).toBe(false);
    expect(parseAudioOn('garbage')).toBe(true);
  });

  it('is on without storage', () => {
    expect(loadAudioOn()).toBe(true);
  });
});

describe('withStereoOpus', () => {
  const answer = [
    'v=0',
    'm=audio 9 UDP/TLS/RTP/SAVPF 111',
    'a=rtpmap:111 opus/48000/2',
    'a=fmtp:111 minptime=10;useinbandfec=1',
    'm=video 9 UDP/TLS/RTP/SAVPF 102',
    'a=rtpmap:102 H264/90000',
    'a=fmtp:102 packetization-mode=1',
    '',
  ].join('\r\n');

  it('asks for stereo on the Opus line only', () => {
    const sdp = withStereoOpus(answer);
    expect(sdp).toContain('a=fmtp:111 minptime=10;useinbandfec=1;stereo=1\r\n');
    expect(sdp).toContain('a=fmtp:102 packetization-mode=1\r\n');
    expect(withStereoOpus(sdp)).toBe(sdp);
  });

  it('leaves an answer without Opus alone', () => {
    const video = 'v=0\r\na=rtpmap:102 H264/90000\r\n';
    expect(withStereoOpus(video)).toBe(video);
  });
});

describe('status bar', () => {
  it('shows video and audio bandwidth, or that the sound is off', () => {
    expect(bandwidthLabel(812.4, 41.6, 'on')).toBe('V 812 · A 42 kbps');
    expect(bandwidthLabel(812.4, null, 'off')).toBe('V 812 kbps · A off');
    expect(bandwidthLabel(null, null, 'none')).toBe('V – kbps');
    expect(bandwidthLabel(700, 0, 'blocked')).toBe('V 700 · A 0 kbps');
  });

  it('labels the ♪ button', () => {
    expect(audioButtonLabel('on')).toBe('♪');
    expect(audioButtonLabel('off')).toBe('♪ off');
    expect(audioButtonLabel('blocked')).toBe('♪ tap');
  });
});

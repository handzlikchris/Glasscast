// M0 WebRTC spike client.
// Receives the server's test-pattern track, reads the timestamp barcode back
// out of the decoded pixels and reports latency, fps, decode time, codec and
// the ICE path in use. Results are also exposed on window.spikeResults() for
// the automated end-to-end check.
'use strict';

// Must match TimestampBarcode.cs.
const DATA_BITS = 24;
const CHECKSUM_BITS = 4;
const TOTAL_BITS = DATA_BITS + CHECKSUM_BITS;
const BLOCK_WIDTH = 21;
const BLOCK_HEIGHT = 24;
const WRAP = 1 << DATA_BITS;

const TARGET_LATENCY_MS = 150;
const TARGET_FPS = 15;
const MIN_SAMPLES = 30;

const $ = (id) => document.getElementById(id);
const video = $('video');
const probe = $('probe');
const probeCtx = probe.getContext('2d', { willReadFrequently: true });

let ws = null;
let pc = null;
let timers = [];

const state = {
  status: 'idle',
  clockOffsetMs: null,  // serverTime - localTime, from the lowest-RTT ping
  bestPingRtt: Infinity,
  latencies: [],
  fps: null,
  decodeMs: null,
  codec: null,
  path: null,
  rttMs: null,
  server: null,
  lastDecode: null,
  barcodeErrors: 0,
};

function setStatus(text) {
  state.status = text;
  $('status').textContent = text;
}

function connect() {
  teardown();
  setStatus('connecting…');
  state.latencies = [];
  state.bestPingRtt = Infinity;
  state.clockOffsetMs = null;

  const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${scheme}://${location.host}/ws/spike`);
  ws.onopen = () => setStatus('signalling…');
  ws.onclose = () => setStatus('disconnected');
  ws.onerror = () => setStatus('signalling error');
  ws.onmessage = (event) => onSignal(JSON.parse(event.data));

  pc = new RTCPeerConnection({ iceServers: [] });
  pc.ontrack = (event) => {
    video.srcObject = event.streams[0] ?? new MediaStream([event.track]);
  };
  pc.onicecandidate = (event) => {
    if (event.candidate) {
      send({
        type: 'candidate',
        candidate: event.candidate.candidate,
        sdpMid: event.candidate.sdpMid,
        sdpMLineIndex: event.candidate.sdpMLineIndex,
      });
    }
  };
  pc.onconnectionstatechange = () => setStatus(pc.connectionState);

  timers.push(setInterval(ping, 2000));
  timers.push(setInterval(collectStats, 1000));
  ping();
}

function teardown() {
  timers.forEach(clearInterval);
  timers = [];
  if (pc) pc.close();
  if (ws) ws.close();
  pc = null;
  ws = null;
}

function send(message) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(message));
  }
}

async function onSignal(message) {
  switch (message.type) {
    case 'offer':
      await pc.setRemoteDescription({ type: 'offer', sdp: message.sdp });
      await pc.setLocalDescription(await pc.createAnswer());
      send({ type: 'answer', sdp: pc.localDescription.sdp });
      break;
    case 'pong':
      onPong(message);
      break;
    case 'serverStats':
      state.server = message;
      $('server').textContent = `${message.fps} fps · ${message.kbps} kb/s · ${message.renderEncodeMs} ms`;
      break;
    case 'error':
      setStatus(`server error: ${message.message}`);
      break;
  }
}

function ping() {
  send({ type: 'ping', t: Date.now() });
}

function onPong(message) {
  const now = Date.now();
  const rtt = now - message.t;
  // Keep the offset from the tightest round trip; it has the least uncertainty.
  if (rtt <= state.bestPingRtt) {
    state.bestPingRtt = rtt;
    state.clockOffsetMs = message.serverTime - (message.t + now) / 2;
  }
}

function decodeBarcode() {
  const vw = video.videoWidth;
  const vh = video.videoHeight;
  if (!vw || !vh) return null;

  // Scale the top strip to a 600px-wide probe so block positions are fixed
  // even if the browser receives a downscaled stream.
  const stripHeight = (BLOCK_HEIGHT * vh) / 600;
  probeCtx.drawImage(video, 0, 0, vw, stripHeight, 0, 0, 600, BLOCK_HEIGHT);
  const pixels = probeCtx.getImageData(0, 0, 600, BLOCK_HEIGHT).data;

  let value = 0;
  let checksum = 0;
  for (let i = 0; i < TOTAL_BITS; i++) {
    const x = i * BLOCK_WIDTH + (BLOCK_WIDTH >> 1);
    const y = BLOCK_HEIGHT >> 1;
    const o = (y * 600 + x) * 4;
    const luma = 0.299 * pixels[o] + 0.587 * pixels[o + 1] + 0.114 * pixels[o + 2];
    const bit = luma > 128 ? 1 : 0;
    if (i < DATA_BITS) value = (value << 1) | bit;
    else checksum = (checksum << 1) | bit;
  }

  let sum = 0;
  for (let shift = 0; shift < DATA_BITS; shift += 4) sum += (value >> shift) & 0xf;
  return (sum & 0xf) === checksum ? value : null;
}

function onFrame() {
  if (state.clockOffsetMs !== null) {
    const code = decodeBarcode();
    if (code === null) {
      state.barcodeErrors++;
    } else {
      const serverNow = Date.now() + state.clockOffsetMs;
      const serverNowWrapped = ((Math.round(serverNow) % WRAP) + WRAP) % WRAP;
      const latency = (serverNowWrapped - code + WRAP) % WRAP;
      if (latency < 10_000) {
        state.latencies.push(latency);
        if (state.latencies.length > 300) state.latencies.shift();
      }
    }
  }
  scheduleFrame();
}

function scheduleFrame() {
  if ('requestVideoFrameCallback' in HTMLVideoElement.prototype) {
    video.requestVideoFrameCallback(onFrame);
  } else {
    requestAnimationFrame(onFrame);
  }
}

function percentile(values, p) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
}

async function collectStats() {
  if (!pc) return;
  const report = await pc.getStats();
  let inbound = null;
  let selectedPairId = null;
  const byId = new Map();

  report.forEach((s) => {
    byId.set(s.id, s);
    if (s.type === 'inbound-rtp' && s.kind === 'video') inbound = s;
    if (s.type === 'transport' && s.selectedCandidatePairId) selectedPairId = s.selectedCandidatePairId;
  });

  if (!selectedPairId) {
    report.forEach((s) => {
      if (s.type === 'candidate-pair' && s.nominated && s.state === 'succeeded') selectedPairId = s.id;
    });
  }

  if (inbound) {
    state.fps = inbound.framesPerSecond ?? null;
    const prev = state.lastDecode;
    if (prev && inbound.framesDecoded > prev.frames) {
      state.decodeMs = ((inbound.totalDecodeTime - prev.time) / (inbound.framesDecoded - prev.frames)) * 1000;
    }
    state.lastDecode = { frames: inbound.framesDecoded, time: inbound.totalDecodeTime ?? 0 };
    const codec = byId.get(inbound.codecId);
    state.codec = codec ? codec.mimeType.replace('video/', '') : null;
  }

  const pair = selectedPairId ? byId.get(selectedPairId) : null;
  if (pair) {
    const local = byId.get(pair.localCandidateId);
    const remote = byId.get(pair.remoteCandidateId);
    const fmt = (c) => (c ? `${c.address ?? c.ip}:${c.port} (${c.candidateType})` : '?');
    state.path = `${fmt(local)} → ${fmt(remote)}`;
    state.rttMs = pair.currentRoundTripTime != null ? pair.currentRoundTripTime * 1000 : null;
  }

  render();
}

function render() {
  const p50 = percentile(state.latencies, 50);
  const p95 = percentile(state.latencies, 95);
  $('latency').textContent = p50 === null ? '–' : `${p50} ms (p95 ${p95})`;
  $('fps').textContent = state.fps === null ? '–' : state.fps.toFixed(0);
  $('decode').textContent = state.decodeMs === null ? '–' : `${state.decodeMs.toFixed(1)} ms`;
  $('codec').textContent = state.codec ?? '–';
  $('rtt').textContent = state.rttMs === null ? '–' : `${state.rttMs.toFixed(0)} ms`;
  $('path').textContent = state.path ?? '–';

  const verdict = $('verdict');
  if (state.latencies.length < MIN_SAMPLES || state.fps === null) {
    verdict.className = 'wait';
    verdict.textContent = `measuring… (${state.latencies.length}/${MIN_SAMPLES} samples)`;
  } else if (p50 < TARGET_LATENCY_MS && state.fps >= TARGET_FPS) {
    verdict.className = 'pass';
    verdict.textContent = `PASS · target < ${TARGET_LATENCY_MS} ms and ≥ ${TARGET_FPS} fps`;
  } else {
    verdict.className = 'fail';
    verdict.textContent = `BELOW TARGET · < ${TARGET_LATENCY_MS} ms and ≥ ${TARGET_FPS} fps`;
  }
}

window.spikeResults = () => ({
  status: state.status,
  samples: state.latencies.length,
  latencyP50: percentile(state.latencies, 50),
  latencyP95: percentile(state.latencies, 95),
  fps: state.fps,
  decodeMs: state.decodeMs,
  codec: state.codec,
  path: state.path,
  rttMs: state.rttMs,
  barcodeErrors: state.barcodeErrors,
  server: state.server,
});

$('reconnect').addEventListener('click', connect);
scheduleFrame();
connect();

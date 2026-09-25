import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent } from 'react';
import { Session } from './connection';
import { centreOf, moveCursorWithEdgePan, ScrollAccumulator, toNormalized, type PanRoom } from './controls';
import {
  ASPECTS,
  clampRegion,
  contentRect,
  moveRegion,
  regionInOverview,
  resizeRegion,
  type AspectName,
  type Point,
  type Rect,
} from './geometry';
import { DEFAULT_GESTURES, GestureTracker, type GestureEvent } from './gestures';
import { armedAfterDrag, armedAfterPress, enterIsSamePinch, NAV_KEYS, routeTap } from './focusnav';
import { drawOverlay, type Look } from './overlay';
import type { ClientMessage, KeyName, Region, ServerMessage, Size, ViewMode } from './protocol';
import { VideoReceiver } from './rtc';
import { TypePanel } from './TypePanel';

/** View pixels of cursor travel per pixel of pinch-drag. Tune on the device. */
const POINTER_GAIN = 1.0;
const SCROLL_FLUSH_MS = 50;
/** Depth (view px) of the band along each edge where pushing further pans the view. */
const EDGE_ZONE = 24;
/** Region updates while panning are sent at most this often. */
const PAN_SEND_MS = 100;
const PAN_GLOW_MS = 250;
const NO_ROOM: PanRoom = { left: false, right: false, up: false, down: false };
const PING_MS = 2000;

const MODES: { mode: ViewMode; label: string }[] = [
  { mode: 'overview', label: 'Overview' },
  { mode: 'view', label: 'View' },
  { mode: 'pointer', label: 'Pointer' },
  { mode: 'scroll', label: 'Scroll' },
  { mode: 'type', label: 'Type' },
];

const LOOKS: Look[] = ['natural', 'lifted', 'contrast'];

const HINTS: Partial<Record<ViewMode, string>> = {
  overview: 'Pinch-drag to move the box',
  pointer: 'Pinch-drag to move · short pinch to click · push past an edge to pan',
  scroll: 'Pinch-drag up or down to scroll',
};

interface Props {
  onEnded(reason: string): void;
}

interface Status {
  media: RTCPeerConnectionState | 'waiting';
  fps: number | null;
  rttMs: number | null;
  codec: string | null;
}

export function SessionScreen({ onEnded }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const stageRef = useRef<HTMLDivElement>(null);
  const sessionRef = useRef<Session | null>(null);
  const gestures = useRef(new GestureTracker(DEFAULT_GESTURES));
  const scroll = useRef(new ScrollAccumulator());
  const pendingMove = useRef<Point | null>(null);
  const moveScheduled = useRef(false);
  const pendingRegion = useRef<Region | null>(null);
  const regionTimer = useRef<number | null>(null);
  const unconfirmedRegions = useRef(0);
  const glowTimer = useRef<number | null>(null);
  // Pinch-to-press on the glasses (see focusnav.ts).
  const focused = useRef<HTMLElement | null>(null);
  const armed = useRef(false);
  const lastPointerAt = useRef(-Infinity);
  const lastEnterAt = useRef(-Infinity);
  const pointerType = useRef('');

  const [monitor, setMonitor] = useState<Size | null>(null);
  const [region, setRegion] = useState<Region | null>(null);
  const [draft, setDraft] = useState<Region | null>(null);
  const [aspect, setAspect] = useState<AspectName>('square');
  const [mode, setModeState] = useState<ViewMode>('view');
  const [cursor, setCursor] = useState<Point | null>(null);
  const [look, setLook] = useState<Look>('natural');
  const [panEdge, setPanEdge] = useState<Point | null>(null);
  const [status, setStatus] = useState<Status>({ media: 'waiting', fps: null, rttMs: null, codec: null });
  /** Last input seen, shown in the status bar while we learn what the glasses send. */
  const [lastInput, setLastInput] = useState('');

  const content: Rect | null = useMemo(() => (region ? contentRect(region) : null), [region]);

  // Latest values for the pointer handlers, which must not go stale between renders.
  const live = useRef({ mode, monitor, region, draft, cursor, content });
  live.current = { mode, monitor, region, draft, cursor, content };

  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  const send = useCallback((message: ClientMessage) => sessionRef.current?.send(message), []);

  // ---- connection lifecycle ----
  useEffect(() => {
    let receiver: VideoReceiver | null = null;
    let ended = false;

    const end = (reason: string) => {
      if (ended) return;
      ended = true;
      onEndedRef.current(reason);
    };

    const onMessage = (message: ServerMessage) => {
      switch (message.type) {
        case 'hello':
          setMonitor(message.monitor);
          setRegion(message.region);
          setAspect(Math.abs(message.region.width / message.region.height - 1) < 0.05 ? 'square' : 'wide');
          break;
        case 'rtcOffer':
          receiver?.handleOffer(message.sdp).catch(() => end('Could not start the video stream.'));
          break;
        case 'region':
          // Our own pans are applied locally first. Only take the server's copy once every
          // setRegion we sent has been answered, so late echoes can't drag the view back.
          unconfirmedRegions.current = Math.max(0, unconfirmedRegions.current - 1);
          if (unconfirmedRegions.current === 0 && !pendingRegion.current) setRegion(message.region);
          setDraft(null);
          break;
        case 'pong':
          setStatus((s) => ({ ...s, rttMs: Date.now() - message.t }));
          break;
      }
    };

    let session: Session;
    try {
      session = Session.open({ onMessage, onClose: end });
    } catch {
      end('Not paired. Pair again.');
      return;
    }
    sessionRef.current = session;
    receiver = new VideoReceiver(session.send.bind(session), videoRef.current!, (media) => {
      setStatus((s) => ({ ...s, media }));
      if (media === 'failed') end('The video connection failed.');
    });

    const ping = setInterval(() => session.send({ type: 'ping', t: Date.now() }), PING_MS);
    const stats = setInterval(async () => {
      const s = await receiver!.stats();
      setStatus((prev) => ({ ...prev, fps: s.fps, codec: s.codec }));
    }, 1000);
    const scrollFlush = setInterval(() => {
      const dy = scroll.current.take();
      if (dy !== 0) session.send({ type: 'scroll', dy });
    }, SCROLL_FLUSH_MS);

    return () => {
      ended = true;
      clearInterval(ping);
      clearInterval(stats);
      clearInterval(scrollFlush);
      if (regionTimer.current !== null) clearTimeout(regionTimer.current);
      if (glowTimer.current !== null) clearTimeout(glowTimer.current);
      receiver?.close();
      session.close();
      sessionRef.current = null;
    };
  }, []);

  // ---- focus and pinch-to-press ----
  const pressFocused = () => {
    const el = focused.current;
    if (!el || !el.isConnected) return;
    if (el instanceof HTMLButtonElement) {
      armed.current = armedAfterPress((el.dataset.mode as ViewMode | undefined) ?? null);
    }
    el.click();
    if (el.isConnected) el.focus({ preventScroll: true });
  };

  useEffect(() => {
    const stage = stageRef.current!;
    const onFocusIn = (e: FocusEvent) => {
      const t = e.target;
      if (!(t instanceof HTMLButtonElement || t instanceof HTMLTextAreaElement)) return;
      focused.current = t;
      // Focus moved by a swipe (keyboard-style) shows the focus ring; a mouse click doesn't.
      if (t.matches(':focus-visible')) armed.current = true;
    };
    const onKeyDown = (e: KeyboardEvent) => {
      setLastInput(`key ${e.key}`);
      if (NAV_KEYS.has(e.key)) armed.current = true;
      // Enter in the text box is typing, never a pinch.
      if (e.key !== 'Enter' || e.target instanceof HTMLTextAreaElement) return;

      const now = performance.now();
      if (enterIsSamePinch(now - lastPointerAt.current)) {
        // The pointer path already handled this pinch.
        e.preventDefault();
        e.stopPropagation();
        return;
      }
      lastEnterAt.current = now;
      const active = document.activeElement;
      if (active instanceof HTMLButtonElement) {
        armed.current = armedAfterPress((active.dataset.mode as ViewMode | undefined) ?? null);
      } else if (!(active instanceof HTMLTextAreaElement) && armed.current && focused.current?.isConnected) {
        // Focus slipped to the page; press the button the swipe last landed on.
        e.preventDefault();
        pressFocused();
      }
    };
    stage.addEventListener('focusin', onFocusIn);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      stage.removeEventListener('focusin', onFocusIn);
      document.removeEventListener('keydown', onKeyDown, true);
    };
  }, []);

  // ---- overlay drawing ----
  const regionBox = mode === 'overview' && draft && monitor ? regionInOverview(draft, monitor) : null;
  const shownCursor = mode === 'pointer' ? cursor : null;

  useEffect(() => {
    const ctx = canvasRef.current?.getContext('2d');
    const edgeGlow = mode === 'pointer' && panEdge && content ? { rect: content, x: panEdge.x, y: panEdge.y } : null;
    if (ctx) drawOverlay(ctx, { cursor: shownCursor, regionBox, look, edgeGlow });
  }, [shownCursor, regionBox?.x, regionBox?.y, regionBox?.width, regionBox?.height, look, panEdge, content, mode]);

  // ---- modes ----
  const setMode = (next: ViewMode) => {
    send({ type: 'setMode', mode: next });
    setModeState(next);
    if (next === 'overview') setDraft(region);
    if (next === 'pointer' && !cursor && content) setCursor(centreOf(content));
  };

  const sendRegion = (r: Region) => {
    unconfirmedRegions.current++;
    send({ type: 'setRegion', x: r.x, y: r.y, width: r.width, height: r.height });
  };

  /** Throttled region updates while panning: the latest wins, at most one per PAN_SEND_MS. */
  const queueRegion = (r: Region) => {
    pendingRegion.current = r;
    if (regionTimer.current !== null) return;
    const flush = () => {
      const next = pendingRegion.current;
      pendingRegion.current = null;
      if (next) {
        sendRegion(next);
        regionTimer.current = window.setTimeout(flush, PAN_SEND_MS);
      } else {
        regionTimer.current = null;
      }
    };
    flush();
  };

  const flushRegion = () => {
    const next = pendingRegion.current;
    pendingRegion.current = null;
    if (next) sendRegion(next);
  };

  const showPanEdge = (x: number, y: number) => {
    setPanEdge({ x, y });
    if (glowTimer.current !== null) clearTimeout(glowTimer.current);
    glowTimer.current = window.setTimeout(() => setPanEdge(null), PAN_GLOW_MS);
  };

  const commitRegion = () => {
    if (draft) sendRegion(draft);
    setMode('view');
  };

  const changeAspect = (next: AspectName) => {
    setAspect(next);
    if (draft && monitor) setDraft(resizeRegion(draft, 1, ASPECTS[next], monitor));
  };

  const scaleDraft = (factor: number) => {
    if (draft && monitor) setDraft(resizeRegion(draft, factor, ASPECTS[aspect], monitor));
  };

  // ---- gestures ----
  const queueMove = (normalized: Point) => {
    pendingMove.current = normalized;
    if (moveScheduled.current) return;
    moveScheduled.current = true;
    requestAnimationFrame(() => {
      moveScheduled.current = false;
      const p = pendingMove.current;
      pendingMove.current = null;
      if (p) send({ type: 'move', x: p.x, y: p.y });
    });
  };

  const flushMove = () => {
    const p = pendingMove.current;
    pendingMove.current = null;
    if (p) send({ type: 'move', x: p.x, y: p.y });
  };

  const handleGesture = (event: GestureEvent) => {
    const { mode: m, monitor: mon, region: r, draft: d, cursor: c, content: box } = live.current;

    if (event.kind === 'tap') {
      const route = routeTap({
        armed: armed.current,
        hasFocused: !!focused.current?.isConnected,
        msSinceEnter: performance.now() - lastEnterAt.current,
      });
      setLastInput(`tap (${pointerType.current}) → ${route === 'pressFocused' ? 'button' : route}`);
      if (route === 'ignore') return;
      if (route === 'pressFocused') {
        pressFocused();
        return;
      }
    } else if (event.kind === 'dragStart') {
      armed.current = armedAfterDrag(m, armed.current);
      setLastInput(`drag (${pointerType.current})`);
    }

    if (m === 'overview' && event.kind === 'drag' && d && mon) {
      const moved = moveRegion(d, event.dx, event.dy, mon);
      live.current.draft = moved;
      setDraft(moved);
    } else if (m === 'pointer' && box) {
      if (event.kind === 'drag') {
        const room: PanRoom =
          r && mon
            ? { left: r.x > 0, right: r.x + r.width < mon.width, up: r.y > 0, down: r.y + r.height < mon.height }
            : NO_ROOM;
        const { cursor: next, panX, panY } = moveCursorWithEdgePan(
          c ?? centreOf(box), event.dx, event.dy, POINTER_GAIN, box, EDGE_ZONE, room);

        if ((panX !== 0 || panY !== 0) && r && mon) {
          // View pixels to monitor pixels, then slide the region; clampRegion keeps it on the monitor.
          const scale = r.width / box.width;
          const panned = clampRegion({ ...r, x: r.x + panX * scale, y: r.y + panY * scale }, mon);
          if (panned.x !== r.x || panned.y !== r.y) {
            live.current.region = panned;
            setRegion(panned);
            queueRegion(panned);
            showPanEdge(Math.sign(panX), Math.sign(panY));
          }
        }

        live.current.cursor = next;
        setCursor(next);
        queueMove(toNormalized(next, box));
      } else if (event.kind === 'dragEnd') {
        // Land the final region before the final cursor position.
        flushRegion();
        flushMove();
      } else if (event.kind === 'tap') {
        // Make sure Windows' cursor is where ours is before clicking.
        flushRegion();
        flushMove();
        if (!c) send({ type: 'move', ...toNormalized(centreOf(box), box) });
        send({ type: 'click', button: 'left' });
      }
    } else if (m === 'scroll' && event.kind === 'drag') {
      scroll.current.add(event.dy);
    }
  };

  const toLocal = (e: PointerEvent) => {
    const rect = stageRef.current!.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  };

  const onPointerDown = (e: PointerEvent<HTMLDivElement>) => {
    // Keep focus (and its ring) on the button the swipe landed on.
    e.preventDefault();
    lastPointerAt.current = performance.now();
    pointerType.current = e.pointerType;
    e.currentTarget.setPointerCapture(e.pointerId);
    const p = toLocal(e);
    gestures.current.down(e.pointerId, p.x, p.y, e.timeStamp);
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    const p = toLocal(e);
    gestures.current.move(e.pointerId, p.x, p.y).forEach(handleGesture);
  };
  const onPointerUp = (e: PointerEvent<HTMLDivElement>) => {
    lastPointerAt.current = performance.now();
    const p = toLocal(e);
    gestures.current.up(e.pointerId, p.x, p.y, e.timeStamp).forEach(handleGesture);
  };
  const onPointerCancel = (e: PointerEvent<HTMLDivElement>) => {
    gestures.current.cancel(e.pointerId).forEach(handleGesture);
  };

  // ---- render ----
  const nextLook = LOOKS[(LOOKS.indexOf(look) + 1) % LOOKS.length];
  const mediaOk = status.media === 'connected';

  return (
    <div ref={stageRef} className={`stage look-${look}`}>
      <video ref={videoRef} autoPlay playsInline muted />
      <canvas ref={canvasRef} width={600} height={600} />
      <div
        className="gesture-layer"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerCancel}
        onMouseDown={(e) => e.preventDefault()}
      />

      <nav className="toolbar top" aria-label="Modes">
        {MODES.map(({ mode: m, label }) => (
          <button key={m} type="button" data-mode={m} aria-pressed={mode === m} onClick={() => setMode(m)}>
            {label}
          </button>
        ))}
        <button type="button" onClick={() => setLook(nextLook)} title="Display look">
          Look: {look}
        </button>
      </nav>

      {HINTS[mode] && <div className="hint">{HINTS[mode]}</div>}

      {mode === 'overview' && (
        <nav className="toolbar bottom" aria-label="Region">
          <button type="button" aria-pressed={aspect === 'square'} onClick={() => changeAspect('square')}>
            Square
          </button>
          <button type="button" aria-pressed={aspect === 'wide'} onClick={() => changeAspect('wide')}>
            16:9
          </button>
          <button type="button" onClick={() => scaleDraft(0.85)} aria-label="Smaller">
            −
          </button>
          <button type="button" onClick={() => scaleDraft(1.18)} aria-label="Larger">
            +
          </button>
          <button type="button" onClick={commitRegion}>
            Use region
          </button>
          <button type="button" onClick={() => setMode('view')}>
            Cancel
          </button>
        </nav>
      )}

      {mode === 'type' && (
        <TypePanel
          onSendText={(text) => send({ type: 'typeText', text })}
          onKey={(key: KeyName) => send({ type: 'key', key })}
        />
      )}

      <footer className="status" aria-live="polite">
        <span className={mediaOk ? 'dot' : 'dot warn'}>●</span>
        <span>{mediaOk ? 'live' : status.media === 'waiting' ? 'starting video…' : status.media}</span>
        <span>{status.fps !== null ? `${status.fps.toFixed(0)} fps` : '– fps'}</span>
        <span>{status.rttMs !== null ? `${status.rttMs} ms` : '– ms'}</span>
        <span>{status.codec ?? ''}</span>
        <span className="input-trace">{lastInput}</span>
        <span style={{ marginLeft: 'auto' }}>{mode}</span>
      </footer>
    </div>
  );
}

import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent } from 'react';
import { Session } from './connection';
import { centreOf, moveCursor, ScrollAccumulator, toNormalized } from './controls';
import {
  ASPECTS,
  contentRect,
  moveRegion,
  regionInOverview,
  resizeRegion,
  type AspectName,
  type Point,
  type Rect,
} from './geometry';
import { DEFAULT_GESTURES, GestureTracker, type GestureEvent } from './gestures';
import { drawOverlay, type Look } from './overlay';
import type { ClientMessage, KeyName, Region, ServerMessage, Size, ViewMode } from './protocol';
import { VideoReceiver } from './rtc';
import { TypePanel } from './TypePanel';

/** View pixels of cursor travel per pixel of pinch-drag. Tune on the device. */
const POINTER_GAIN = 1.0;
const SCROLL_FLUSH_MS = 50;
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
  pointer: 'Pinch-drag to move · short pinch to click',
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

  const [monitor, setMonitor] = useState<Size | null>(null);
  const [region, setRegion] = useState<Region | null>(null);
  const [draft, setDraft] = useState<Region | null>(null);
  const [aspect, setAspect] = useState<AspectName>('square');
  const [mode, setModeState] = useState<ViewMode>('view');
  const [cursor, setCursor] = useState<Point | null>(null);
  const [look, setLook] = useState<Look>('natural');
  const [status, setStatus] = useState<Status>({ media: 'waiting', fps: null, rttMs: null, codec: null });

  const content: Rect | null = useMemo(() => (region ? contentRect(region) : null), [region]);

  // Latest values for the pointer handlers, which must not go stale between renders.
  const live = useRef({ mode, monitor, draft, cursor, content });
  live.current = { mode, monitor, draft, cursor, content };

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
          setRegion(message.region);
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
      receiver?.close();
      session.close();
      sessionRef.current = null;
    };
  }, []);

  // ---- overlay drawing ----
  const regionBox = mode === 'overview' && draft && monitor ? regionInOverview(draft, monitor) : null;
  const shownCursor = mode === 'pointer' ? cursor : null;

  useEffect(() => {
    const ctx = canvasRef.current?.getContext('2d');
    if (ctx) drawOverlay(ctx, { cursor: shownCursor, regionBox, look });
  }, [shownCursor, regionBox?.x, regionBox?.y, regionBox?.width, regionBox?.height, look]);

  // ---- modes ----
  const setMode = (next: ViewMode) => {
    send({ type: 'setMode', mode: next });
    setModeState(next);
    if (next === 'overview') setDraft(region);
    if (next === 'pointer' && !cursor && content) setCursor(centreOf(content));
  };

  const commitRegion = () => {
    if (draft) send({ type: 'setRegion', ...draft });
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
    const { mode: m, monitor: mon, draft: d, cursor: c, content: box } = live.current;

    if (m === 'overview' && event.kind === 'drag' && d && mon) {
      const moved = moveRegion(d, event.dx, event.dy, mon);
      live.current.draft = moved;
      setDraft(moved);
    } else if (m === 'pointer' && box) {
      if (event.kind === 'drag') {
        const next = moveCursor(c ?? centreOf(box), event.dx, event.dy, POINTER_GAIN, box);
        live.current.cursor = next;
        setCursor(next);
        queueMove(toNormalized(next, box));
      } else if (event.kind === 'tap') {
        // Make sure Windows' cursor is where ours is before clicking.
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
    e.currentTarget.setPointerCapture(e.pointerId);
    const p = toLocal(e);
    gestures.current.down(e.pointerId, p.x, p.y, e.timeStamp);
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    const p = toLocal(e);
    gestures.current.move(e.pointerId, p.x, p.y).forEach(handleGesture);
  };
  const onPointerUp = (e: PointerEvent<HTMLDivElement>) => {
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
      />

      <nav className="toolbar top" aria-label="Modes">
        {MODES.map(({ mode: m, label }) => (
          <button key={m} type="button" aria-pressed={mode === m} onClick={() => setMode(m)}>
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
        <span style={{ marginLeft: 'auto' }}>{mode}</span>
      </footer>
    </div>
  );
}

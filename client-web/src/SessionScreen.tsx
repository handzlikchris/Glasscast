import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent } from 'react';
import { Session } from './connection';
import { centreOf, moveCursorWithEdgePan, nudgeRegion, ScrollAccumulator, toNormalized, type PanRoom } from './controls';
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
import {
  ARROW_STEPS,
  enterIsSamePinch,
  menuFocusFor,
  navAfterMode,
  routeTap,
  VIEW_NAV_MODES,
  type NavTarget,
} from './focusnav';
import { DEFAULT_GESTURES, DOUBLE_TAP_MS, GestureTracker, HOLD_MS, TapThenHold, type GestureEvent } from './gestures';
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

// Pointer and Type side by side: most use goes Pointer → Type → Pointer.
const MODES: { mode: ViewMode; label: string }[] = [
  { mode: 'overview', label: 'Overview' },
  { mode: 'view', label: 'View' },
  { mode: 'scroll', label: 'Scroll' },
  { mode: 'pointer', label: 'Pointer' },
  { mode: 'type', label: 'Type' },
];

const LOOKS: Look[] = ['natural', 'lifted', 'contrast'];


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
  // Swipes, pinches and the controls on the glasses (see focusnav.ts).
  const focused = useRef<HTMLElement | null>(null);
  const lastPointerAt = useRef(-Infinity);
  const lastEnterAt = useRef(-Infinity);
  const pointerType = useRef('');
  const tapThenHold = useRef(new TapThenHold());
  const holdTimer = useRef<number | null>(null);
  /**
   * A press that started right after a pinch. Movement is ignored for it: it ends either as a
   * quick second tap (released before HOLD_MS) or, held that long, as "go to the controls".
   */
  const secondPress = useRef<{ id: number; x: number; y: number; held: boolean } | null>(null);
  /** Pointer-mode click held back in case a second pinch follows: a timer, or 'waiting' while that pinch is down. */
  const pendingClick = useRef<number | 'waiting' | null>(null);
  const nudgeRef = useRef<(dx: number, dy: number) => void>(() => {});
  const setNavRef = useRef<(next: NavTarget) => void>(() => {});
  /** Focus was last moved by swipes (arrow keys) or Tab, not by clicking a control. */
  const keyFocus = useRef(false);

  const [monitor, setMonitor] = useState<Size | null>(null);
  const [region, setRegion] = useState<Region | null>(null);
  const [draft, setDraft] = useState<Region | null>(null);
  const [aspect, setAspect] = useState<AspectName>('square');
  // The server says which mode a session starts in (Pointer); this is only until hello arrives.
  const [mode, setModeState] = useState<ViewMode>('pointer');
  const [cursor, setCursor] = useState<Point | null>(null);
  const [look, setLook] = useState<Look>('lifted');
  const [panEdge, setPanEdge] = useState<Point | null>(null);
  const [status, setStatus] = useState<Status>({ media: 'waiting', fps: null, rttMs: null, codec: null });
  /** Last input seen, shown in the status bar while we learn what the glasses send. */
  const [lastInput, setLastInput] = useState('');
  const [nav, setNavState] = useState<NavTarget>('view');

  const content: Rect | null = useMemo(() => (region ? contentRect(region) : null), [region]);

  // Latest values for the pointer handlers, which must not go stale between renders.
  const live = useRef({ mode, monitor, region, draft, cursor, content, nav });
  live.current = { mode, monitor, region, draft, cursor, content, nav };

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
          setModeState(message.mode);
          live.current.mode = message.mode;
          setNavRef.current(navAfterMode(message.mode));
          if (message.mode === 'pointer') setCursor(centreOf(contentRect(message.region)));
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

  // ---- swipes, pinches and the controls ----
  const setNav = (next: NavTarget) => {
    live.current.nav = next;
    setNavState(next);
    if (next === 'view') {
      keyFocus.current = false;
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    }
  };
  setNavRef.current = setNav;

  const pressFocused = () => {
    const el = focused.current;
    if (!el || !el.isConnected) return;
    el.click();
    // Keep the focus ring if the press lost it, unless it sent swipes back to the view or
    // moved focus on purpose (Type → text box, Send text → Pointer).
    const lost = document.activeElement === null || document.activeElement === document.body;
    if (lost && el.isConnected && live.current.nav === 'controls') el.focus({ preventScroll: true });
  };

  /** Moves focus to a mode button without switching modes, so the next pinch picks it. */
  const focusModeButton = (target: ViewMode) => {
    const button = stageRef.current?.querySelector<HTMLButtonElement>(`button[data-mode="${target}"]`);
    if (!button) return;
    focused.current = button;
    button.focus({ preventScroll: true });
  };

  /** Pinch, then pinch and hold: put focus on the current mode's button. */
  const focusControls = () => {
    setNav('controls');
    keyFocus.current = true;
    focusModeButton(menuFocusFor(live.current.mode));
    setLastInput('pinch, hold → controls');
  };

  useEffect(() => {
    const stage = stageRef.current!;
    const onFocusIn = (e: FocusEvent) => {
      const t = e.target;
      if (!(t instanceof HTMLButtonElement || t instanceof HTMLTextAreaElement)) return;
      focused.current = t;
      // Focus moved by keyboard (Tab on a laptop) means you're on the controls; a mouse click doesn't.
      if (t.matches(':focus-visible')) setNav('controls');
    };
    const onKeyDown = (e: KeyboardEvent) => {
      setLastInput(`key ${e.key}`);
      const step = ARROW_STEPS[e.key];
      const { nav: target, mode: m } = live.current;
      if (step && target === 'view' && VIEW_NAV_MODES.includes(m) && !(e.target instanceof HTMLTextAreaElement)) {
        // A swipe moves the view instead of the focus.
        e.preventDefault();
        e.stopPropagation();
        nudgeRef.current(step.dx, step.dy);
        return;
      }
      if (step || e.key === 'Tab') keyFocus.current = true;
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
      if (
        !(active instanceof HTMLButtonElement || active instanceof HTMLTextAreaElement) &&
        live.current.nav === 'controls' &&
        focused.current?.isConnected
      ) {
        // Focus slipped to the page; press the button the swipe last landed on.
        e.preventDefault();
        pressFocused();
      }
    };
    // On the glasses a pinch lands wherever the pointer is, which after typing is often the
    // text box or a panel, not the control the swipes focused. While on the controls, a pinch
    // on anything but the focused control presses the focused control instead. Only when focus
    // was last moved by swipes/keys, so mouse clicks on a laptop are left alone.
    let redirectedPointer: number | null = null;
    let swallowClick = false;
    const onPointerDownCapture = (e: globalThis.PointerEvent) => {
      swallowClick = false;
      const active = document.activeElement;
      const target = e.target;
      if (!(target instanceof Element) || target.classList.contains('gesture-layer')) return; // taps handled there
      const activeControl =
        (active instanceof HTMLButtonElement || active instanceof HTMLTextAreaElement) && stage.contains(active)
          ? active
          : null;
      if (activeControl?.contains(target)) return; // pressing the focused control itself
      if (!(keyFocus.current && live.current.nav === 'controls' && activeControl)) {
        // Clicking a control directly takes over from the keys.
        if (target.closest('button, textarea')) keyFocus.current = false;
        return;
      }
      e.preventDefault();
      e.stopPropagation();
      redirectedPointer = e.pointerId;
      lastPointerAt.current = performance.now();
      focused.current = activeControl;
    };
    const onPointerUpCapture = (e: globalThis.PointerEvent) => {
      if (e.pointerId !== redirectedPointer) return;
      redirectedPointer = null;
      e.stopPropagation();
      lastPointerAt.current = performance.now();
      swallowClick = true;
      setLastInput(`tap (${e.pointerType}) → focused`);
      pressFocused();
    };
    const onClickCapture = (e: MouseEvent) => {
      // The browser's own click for a redirected pinch; our press already happened.
      if (!swallowClick || !e.isTrusted) return;
      swallowClick = false;
      e.preventDefault();
      e.stopPropagation();
    };

    stage.addEventListener('focusin', onFocusIn);
    stage.addEventListener('pointerdown', onPointerDownCapture, true);
    stage.addEventListener('pointerup', onPointerUpCapture, true);
    stage.addEventListener('click', onClickCapture, true);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      stage.removeEventListener('focusin', onFocusIn);
      stage.removeEventListener('pointerdown', onPointerDownCapture, true);
      stage.removeEventListener('pointerup', onPointerUpCapture, true);
      stage.removeEventListener('click', onClickCapture, true);
      document.removeEventListener('keydown', onKeyDown, true);
      if (holdTimer.current !== null) clearTimeout(holdTimer.current);
      if (typeof pendingClick.current === 'number') clearTimeout(pendingClick.current);
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
    live.current.mode = next;
    setNav(navAfterMode(next));
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

  const nudge = (dx: number, dy: number) => {
    const { region: r, monitor: mon } = live.current;
    if (!r || !mon) return;
    const moved = nudgeRegion(r, dx, dy, mon);
    if (moved.x === r.x && moved.y === r.y) return;
    live.current.region = moved;
    setRegion(moved);
    sendRegion(moved);
  };
  nudgeRef.current = nudge;

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

  const clearPendingClick = () => {
    if (typeof pendingClick.current === 'number') clearTimeout(pendingClick.current);
    pendingClick.current = null;
  };

  /** Sends a held-back Pointer-mode click now (a drag or cancel followed the pinch). */
  const releasePendingClick = () => {
    if (pendingClick.current === null) return;
    clearPendingClick();
    send({ type: 'click', button: 'left' });
  };

  const handleGesture = (event: GestureEvent) => {
    const { mode: m, monitor: mon, region: r, draft: d, cursor: c, content: box } = live.current;

    if (event.kind === 'tap') {
      const route = routeTap({
        nav: live.current.nav,
        hasFocused: !!focused.current?.isConnected,
        msSinceEnter: performance.now() - lastEnterAt.current,
      });
      setLastInput(`tap (${pointerType.current}) → ${route === 'pressFocused' ? 'button' : route}`);
      if (route === 'ignore') return;
      tapThenHold.current.tapped(performance.now());
      if (route === 'pressFocused') {
        pressFocused();
        return;
      }
    } else if (event.kind === 'dragStart') {
      releasePendingClick();
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
        if (pendingClick.current !== null) {
          // Second quick pinch: a double-click.
          clearPendingClick();
          send({ type: 'click', button: 'left' });
          send({ type: 'click', button: 'left' });
        } else {
          // Held back briefly: a second pinch held down means "controls", not a click.
          pendingClick.current = window.setTimeout(() => {
            pendingClick.current = null;
            send({ type: 'click', button: 'left' });
          }, DOUBLE_TAP_MS);
        }
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

    // A pinch right after a pinch: hold back the first one's click until this one is decided.
    if (typeof pendingClick.current === 'number') {
      clearTimeout(pendingClick.current);
      pendingClick.current = 'waiting';
    }
    if (tapThenHold.current.pressStarted(performance.now())) {
      const press = { id: e.pointerId, x: p.x, y: p.y, held: false };
      secondPress.current = press;
      clearHold();
      holdTimer.current = window.setTimeout(() => {
        holdTimer.current = null;
        if (secondPress.current !== press) return;
        press.held = true;
        clearPendingClick();
        focusControls();
      }, HOLD_MS);
      return;
    }
    gestures.current.down(e.pointerId, p.x, p.y, e.timeStamp);
  };
  const onPointerMove = (e: PointerEvent<HTMLDivElement>) => {
    if (secondPress.current?.id === e.pointerId) return; // wobble during pinch-then-hold
    const p = toLocal(e);
    gestures.current.move(e.pointerId, p.x, p.y).forEach(handleGesture);
  };
  const clearHold = () => {
    if (holdTimer.current !== null) clearTimeout(holdTimer.current);
    holdTimer.current = null;
  };

  const onPointerUp = (e: PointerEvent<HTMLDivElement>) => {
    lastPointerAt.current = performance.now();
    clearHold();
    const press = secondPress.current;
    if (press?.id === e.pointerId) {
      secondPress.current = null;
      // Released before the hold time: a quick second tap (a double-click in Pointer mode).
      if (!press.held) handleGesture({ kind: 'tap', x: press.x, y: press.y });
      return;
    }
    const p = toLocal(e);
    gestures.current.up(e.pointerId, p.x, p.y, e.timeStamp).forEach(handleGesture);
  };
  const onPointerCancel = (e: PointerEvent<HTMLDivElement>) => {
    clearHold();
    if (secondPress.current?.id === e.pointerId) secondPress.current = null;
    releasePendingClick();
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

      <nav className={nav === 'view' && VIEW_NAV_MODES.includes(mode) ? 'toolbar top dimmed' : 'toolbar top'} aria-label="Modes">
        {MODES.map(({ mode: m, label }) => (
          <button key={m} type="button" data-mode={m} aria-pressed={mode === m} onClick={() => setMode(m)}>
            {label}
          </button>
        ))}
        <button type="button" onClick={() => setLook(nextLook)} title="Display look">
          Look: {look}
        </button>
      </nav>

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
          onKey={(key: KeyName) => {
            send({ type: 'key', key });
            // Enter usually finishes the job: focus Pointer so going back is one pinch.
            if (key === 'Enter') focusModeButton('pointer');
          }}
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

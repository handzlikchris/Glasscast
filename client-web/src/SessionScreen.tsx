import { useCallback, useEffect, useMemo, useRef, useState, type PointerEvent } from 'react';
import { flushSync } from 'react-dom';
import { Session } from './connection';
import {
  centreOf,
  edgeScrollStep,
  moveCursorLocked,
  moveCursorWithEdgePan,
  nudgeRegion,
  ScrollAccumulator,
  toNormalized,
  type EdgeScroll,
  type PanRoom,
} from './controls';
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
  backTarget,
  isSecondLeftSwipe,
  nextAppSlot,
  swipeAction,
  type SwipeAction,
  SAME_BACK_MS,
  navAfterMode,
  routeTap,
  VIEW_NAV_MODES,
  type NavTarget,
} from './focusnav';
import { DEFAULT_GESTURES, DOUBLE_TAP_MS, GestureTracker, HOLD_MS, TapThenHold, type GestureEvent } from './gestures';
import { bandwidthLabel, loadAudioOn, saveAudioOn, type AudioState } from './audio';
import { AudioOutput, type OutputState } from './audioOutput';
import { loadBrightness, nextBrightness, saveBrightness, type Brightness } from './display';
import { ClockSync, FrameLatency, PC_STATS_KEPT, statsLines, statsReport } from './mediaStats';
import {
  edgeUnitsPerSecond,
  levelFor,
  loadScrollLevels,
  nextScrollLevel,
  NO_APP,
  saveScrollLevels,
  swipeUnits,
  type ScrollLevels,
} from './scrollPrefs';
import { drawOverlay, type Look } from './overlay';
import { textChunks, type ClientMessage, type KeyName, type PcMediaStats, type Region, type ServerMessage, type Size, type ViewMode } from './protocol';
import { VideoReceiver, watchFrames } from './rtc';
import { TypePanel } from './TypePanel';

/** View pixels of cursor travel per pixel of pinch-drag. Tune on the device. */
const POINTER_GAIN = 1.0;
const SCROLL_FLUSH_MS = 50;
/** Depth (view px) of the band along each edge where pushing further pans the view. */
const EDGE_ZONE = 24;
/** Region updates while panning are sent at most this often. */
const PAN_SEND_MS = 100;
const PAN_GLOW_MS = 250;
/** While edge scrolling, the cursor waits this far (view px) inside the edge, over the page itself. */
const EDGE_SCROLL_INSET = 24;
const NO_ROOM: PanRoom = { left: false, right: false, up: false, down: false };
const PING_MS = 2000;
/**
 * The app hidden this long (another glasses app, or closed but kept alive) ends the session, so
 * the PC stops showing it as live; Reconnect is a pinch when it's back. Short enough to free the
 * PC quickly, long enough to survive a glance away. A page frozen outright stops pinging instead,
 * and the PC ends the session after its heartbeat timeout (Session:HeartbeatTimeout, 15 s).
 */
const HIDDEN_MS = 5000;
/** How long focus put on the controls is held there against resets we didn't cause (ms). */
const FOCUS_PIN_MS = 600;

// Pointer and Type side by side: most use goes Pointer → Type → Pointer.
const MODES: { mode: ViewMode; label: string }[] = [
  { mode: 'overview', label: 'Region' },
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
  /** The video over the LAN or the internet (shown next to "live"). */
  path: 'local' | 'remote' | null;
  /** Received kbit/s (payload), for the status bar. */
  videoKbps: number | null;
  audioKbps: number | null;
}

export function SessionScreen({ onEnded }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const audioRef = useRef<HTMLAudioElement>(null);
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
  const swipeRef = useRef<(action: SwipeAction) => void>(() => {});
  /** When the first of a double swipe left came (performance.now), or null. */
  const leftSwipeAt = useRef<number | null>(null);
  /** The app last switched to (1-based); swipe left goes to the one after it. Assume app 1 at the start. */
  const currentApp = useRef(1);
  /** The app shown as current on its button: moves only when the PC confirms a switch. */
  const [activeApp, setActiveApp] = useState(1);
  const activeAppRef = useRef(1);
  const setNavRef = useRef<(next: NavTarget) => void>(() => {});
  const backRef = useRef<() => void>(() => {});
  /**
   * Focus we just put on the controls. The glasses reset focus to the first button right after
   * a Back; for a moment, focus taken elsewhere without a swipe or pinch is put back.
   */
  const pin = useRef<{ el: HTMLElement; until: number } | null>(null);
  const restorePinRef = useRef<() => void>(() => {});
  const lastBackAt = useRef(-Infinity);
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
  const [brightness, setBrightness] = useState<Brightness>(loadBrightness);
  const [panEdge, setPanEdge] = useState<Point | null>(null);
  const [status, setStatus] = useState<Status>({
    media: 'waiting',
    fps: null,
    rttMs: null,
    codec: null,
    path: null,
    videoKbps: null,
    audioKbps: null,
  });
  /** The PC's sound: whether the PC offers it, the ♪ setting (remembered), and whether the browser is waiting for a tap. */
  const [audioOffered, setAudioOffered] = useState(false);
  const [audioOn, setAudioOn] = useState(loadAudioOn);
  const audioOnRef = useRef(audioOn);
  const [audioBlocked, setAudioBlocked] = useState(false);
  /** Plays the sound through Web Audio (see audioOutput.ts); its state shows on the ♪ button. */
  const audioOut = useRef<AudioOutput | null>(null);
  const [outputState, setOutputState] = useState<OutputState>('none');
  /** Last input seen, shown in the status bar while we learn what the glasses send. */
  const [lastInput, setLastInput] = useState('');
  /** App shortcut names configured on the PC; button N switches to app N. */
  const [apps, setApps] = useState<string[]>([]);
  const appsRef = useRef<string[]>([]);
  const [nav, setNavState] = useState<NavTarget>('view');
  /** Pointer mode only: swipes pan the view (on) or act as shortcuts (off, the default). */
  const [panSwipes, setPanSwipes] = useState(false);
  /** Scroll strength per app (swipe step and edge-scroll speed), remembered on this device. */
  const [scrollLevels, setScrollLevels] = useState<ScrollLevels>(loadScrollLevels);
  const scrollLevelsRef = useRef(scrollLevels);
  /** Hold-to-scroll at the top or bottom edge while dragging (Pan off). */
  const edgeScroll = useRef<EdgeScroll | null>(null);
  /** Where the glasses' own pointer is (stage px): pushed against the display edge it stops moving. */
  const lastPointer = useRef<{ x: number; y: number } | null>(null);
  /** Latency measurement (see mediaStats.ts); collected all the time, shown by the Stats button. */
  const clock = useRef(new ClockSync());
  const latency = useRef(new FrameLatency(clock.current));
  const pcStats = useRef<PcMediaStats[]>([]);
  const [showStats, setShowStats] = useState(false);
  const [statsText, setStatsText] = useState<string[]>([]);

  const content: Rect | null = useMemo(() => (region ? contentRect(region) : null), [region]);

  // Latest values for the pointer handlers, which must not go stale between renders.
  const live = useRef({ mode, monitor, region, draft, cursor, content, nav, panSwipes });
  live.current = { mode, monitor, region, draft, cursor, content, nav, panSwipes };

  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

  const send = useCallback((message: ClientMessage) => sessionRef.current?.send(message), []);

  const output = useCallback(() => (audioOut.current ??= new AudioOutput(audioRef.current!)), []);

  /**
   * Plays the PC's sound if ♪ is on (silences it if off). Browsers only start sound after a user
   * gesture: after Pair or Reconnect it plays at once; after a page reload it may wait for the
   * next pinch or swipe.
   */
  const playAudio = useCallback(() => {
    const out = output();
    const on = audioOnRef.current;
    void out.play(on).then((running) => {
      setAudioBlocked(on && !running);
      setOutputState(out.state);
    });
  }, [output]);

  useEffect(() => {
    if (!audioBlocked) return;
    // Any gesture counts as the user's permission to play: retry on the next one.
    const retry = () => playAudio();
    window.addEventListener('pointerdown', retry, true);
    window.addEventListener('keydown', retry, true);
    return () => {
      window.removeEventListener('pointerdown', retry, true);
      window.removeEventListener('keydown', retry, true);
    };
  }, [audioBlocked, playAudio]);

  const toggleAudio = () => {
    const next = !audioOnRef.current;
    audioOnRef.current = next;
    setAudioOn(next);
    saveAudioOn(next);
    const out = output();
    // Called inside the pinch, so the browser lets the sound start.
    void out.play(next).then((running) => {
      setAudioBlocked(next && !running);
      setOutputState(out.state);
      setLastInput(next ? `sound on (web audio ${out.state})` : 'sound off');
    });
    // Off stops the PC capturing and sending too: the bandwidth goes to the video.
    send({ type: 'setAudio', enabled: next });
  };

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
          setApps(message.apps);
          appsRef.current = message.apps;
          setModeState(message.mode);
          live.current.mode = message.mode;
          setNavRef.current(navAfterMode(message.mode));
          if (message.mode === 'pointer') setCursor(centreOf(contentRect(message.region)));
          setAudioOffered(message.audio);
          // The PC starts with its sound off and waits for the ♪ setting.
          if (message.audio) sessionRef.current?.send({ type: 'setAudio', enabled: audioOnRef.current });
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
        case 'pong': {
          const rtt = Date.now() - message.t;
          const receivedAt = performance.timeOrigin + performance.now();
          clock.current.add(receivedAt - rtt, receivedAt, message.serverTime);
          setStatus((s) => ({ ...s, rttMs: rtt }));
          break;
        }
        case 'mediaStats':
          latency.current.addSent(message.frames, performance.timeOrigin + performance.now());
          pcStats.current = [...pcStats.current, message].slice(-PC_STATS_KEPT);
          break;
        case 'appSwitch': {
          const name = appsRef.current[message.slot - 1] ?? `app ${message.slot}`;
          const outcome = { switched: 'switched', notRunning: 'not open', failed: 'failed' }[message.result];
          setLastInput(`${name}: ${outcome}`);
          if (message.result === 'switched') {
            activeAppRef.current = message.slot;
            setActiveApp(message.slot);
          } else {
            // Didn't happen: the next swipe counts on from the app that really is in front.
            currentApp.current = activeAppRef.current;
          }
          break;
        }
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
    receiver = new VideoReceiver(
      session.send.bind(session),
      videoRef.current!,
      (media) => {
        setStatus((s) => ({ ...s, media }));
        if (media === 'failed') end('The video connection failed.');
      },
      (stream) => {
        output().attach(stream);
        playAudio();
      },
    );

    const stopWatchingFrames = watchFrames(videoRef.current!, (frame) => latency.current.addShown(frame));
    // Ping at once too: the clock offset for the latency figures comes from pongs.
    session.send({ type: 'ping', t: Date.now() });
    const ping = setInterval(() => session.send({ type: 'ping', t: Date.now() }), PING_MS);
    const stats = setInterval(async () => {
      const s = await receiver!.stats();
      setStatus((prev) => ({
        ...prev,
        fps: s.fps,
        codec: s.codec,
        path: s.path,
        videoKbps: s.receiver?.kbps ?? null,
        audioKbps: s.audio?.kbps ?? null,
      }));
      const now = performance.timeOrigin + performance.now();
      const summary = latency.current.summary(now);
      setStatsText(statsLines(summary, s.receiver, pcStats.current, s.network, s.audio));
      // Also to the PC's stats log, so a session can be read back there afterwards. Only once the
      // PC has sent mediaStats: an older server would reject the message and end the session.
      if (s.receiver && pcStats.current.length > 0) session.send({ type: 'stats', ...statsReport(summary, s.receiver, s.fps, latency.current.shownCount, s.network, s.audio) });
    }, 1000);
    let hiddenTimer: ReturnType<typeof setTimeout> | null = null;
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') {
        hiddenTimer ??= setTimeout(() => end('The session was closed while the app was hidden.'), HIDDEN_MS);
      } else if (hiddenTimer !== null) {
        clearTimeout(hiddenTimer);
        hiddenTimer = null;
      }
    };
    document.addEventListener('visibilitychange', onVisibility);
    const scrollFlush = setInterval(() => {
      const edge = edgeScroll.current;
      if (edge) scroll.current.addUnits((edge.dir * edgeUnitsPerSecond(currentScrollLevel()) * SCROLL_FLUSH_MS) / 1000);
      const dy = scroll.current.take();
      if (dy !== 0) session.send({ type: 'scroll', dy });
    }, SCROLL_FLUSH_MS);

    return () => {
      ended = true;
      document.removeEventListener('visibilitychange', onVisibility);
      if (hiddenTimer !== null) clearTimeout(hiddenTimer);
      stopWatchingFrames();
      clearInterval(ping);
      clearInterval(stats);
      clearInterval(scrollFlush);
      if (regionTimer.current !== null) clearTimeout(regionTimer.current);
      if (glowTimer.current !== null) clearTimeout(glowTimer.current);
      receiver?.close();
      audioOut.current?.close();
      audioOut.current = null;
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
      pin.current = null;
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
    }
  };
  setNavRef.current = setNav;

  const pressFocused = () => {
    const el = focused.current;
    if (!el || !el.isConnected) return;
    pin.current = null;
    el.click();
    // Keep the focus ring if the press lost it, unless it sent swipes back to the view or
    // moved focus on purpose (Type → text box, Send text → Pointer).
    const lost = document.activeElement === null || document.activeElement === document.body;
    if (lost && el.isConnected && live.current.nav === 'controls') el.focus({ preventScroll: true });
  };

  /** Moves focus to a mode button without switching modes, so the next pinch picks it. */
  /**
   * Moves focus for you (the next step of a flow) and holds it there briefly: the glasses
   * sometimes reset focus right after a navigation or when the composer closes.
   */
  const focusPinned = (el: HTMLElement) => {
    focused.current = el;
    el.focus({ preventScroll: true });
    pin.current = { el, until: performance.now() + FOCUS_PIN_MS };
    for (const delay of [50, 150, 300, 500]) window.setTimeout(() => restorePinRef.current(), delay);
  };

  const focusModeButton = (target: ViewMode) => {
    const button = stageRef.current?.querySelector<HTMLButtonElement>(`button[data-mode="${target}"]`);
    if (button) focusPinned(button);
  };

  /** Pinch, then pinch and hold: put focus on the current mode's button. */
  const focusControls = (how = 'pinch, hold') => {
    setNav('controls');
    keyFocus.current = true;
    focusModeButton(menuFocusFor(live.current.mode));
    setLastInput(`${how} → controls`);
  };

  const restorePin = () => {
    const p = pin.current;
    if (!p || performance.now() > p.until || !p.el.isConnected) return;
    if (document.activeElement !== p.el) {
      focused.current = p.el;
      p.el.focus({ preventScroll: true });
    }
  };
  restorePinRef.current = restorePin;

  /** Back: to the controls, or from them back to the view. One gesture may arrive twice. */
  const onBack = () => {
    const now = performance.now();
    if (now - lastBackAt.current < SAME_BACK_MS) return;
    lastBackAt.current = now;
    if (backTarget(live.current.nav) === 'pointer') {
      // Home: from Type, Overview or the mode bar straight back to Pointer (unsent text is dropped).
      setMode('pointer');
      setLastInput('back → pointer');
    } else {
      focusControls('back');
    }
  };
  backRef.current = onBack;

  useEffect(() => {
    const stage = stageRef.current!;
    const onFocusIn = (e: FocusEvent) => {
      const t = e.target;
      if (!(t instanceof HTMLButtonElement || t instanceof HTMLTextAreaElement)) return;
      if (pin.current && t !== pin.current.el) window.setTimeout(() => restorePinRef.current(), 0);
      focused.current = t;
      // Focus moved by keyboard (Tab on a laptop) means you're on the controls; a mouse click doesn't.
      if (t.matches(':focus-visible')) setNav('controls');
    };
    const onKeyDown = (e: KeyboardEvent) => {
      setLastInput(`key ${e.key}`);
      // Swiping or tabbing yourself ends the pin on focus.
      if (e.key in ARROW_STEPS || e.key === 'Tab') pin.current = null;
      if ((e.key === 'Escape' || e.key === 'Backspace') && !(e.target instanceof HTMLTextAreaElement)) {
        // The glasses' Back, as a key.
        e.preventDefault();
        e.stopPropagation();
        backRef.current();
        return;
      }
      const { nav: target, mode: m, panSwipes: pan } = live.current;
      const onView = target === 'view' && VIEW_NAV_MODES.includes(m) && !(e.target instanceof HTMLTextAreaElement);
      const action = onView ? swipeAction(e.key, m, pan) : null;
      if (action) {
        // A swipe acts on the view (pan, or a Pointer-mode shortcut) instead of moving the focus.
        e.preventDefault();
        e.stopPropagation();
        swipeRef.current(action);
        return;
      }
      if (e.key in ARROW_STEPS || e.key === 'Tab') keyFocus.current = true;
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
      pin.current = null;
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

  // The glasses only call history.back() for Back when the page has an entry to go back to (at the
  // root they show the system menu), so keep one extra entry for the whole session and put it back
  // after each Back. Leaving the session drops it, so Back opens the menu again.
  useEffect(() => {
    history.pushState({ glassesBack: true }, '');
    const onPopState = () => {
      history.pushState({ glassesBack: true }, '');
      backRef.current();
    };
    window.addEventListener('popstate', onPopState);
    return () => {
      window.removeEventListener('popstate', onPopState);
      if ((history.state as { glassesBack?: boolean } | null)?.glassesBack) history.back();
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
    // Type renders its panel at once, so it can click into the text box while this pinch or
    // swipe is still being handled: the glasses may then count it as yours and open the composer.
    if (next === 'type') flushSync(() => setModeState(next));
    else setModeState(next);
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

  /** The app shown as current (moves on a confirmed switch); levels are kept by its name. */
  const currentAppName = () => appsRef.current[activeAppRef.current - 1] ?? NO_APP;
  const currentScrollLevel = () => levelFor(scrollLevelsRef.current, currentAppName());

  const cycleScrollLevel = () => {
    const app = currentAppName();
    const next = nextScrollLevel(levelFor(scrollLevelsRef.current, app));
    const levels = { ...scrollLevelsRef.current, [app]: next };
    scrollLevelsRef.current = levels;
    setScrollLevels(levels);
    saveScrollLevels(levels);
    setLastInput(`${app || 'scroll'}: ${next} notch${next === 1 ? '' : 'es'} per swipe`);
  };

  const stopEdgeScroll = () => {
    if (!edgeScroll.current) return;
    edgeScroll.current = null;
    setPanEdge(null);
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

  const onSwipe = (action: SwipeAction) => {
    // Any other swipe in between cancels a half-done double swipe left.
    if (action.kind !== 'nextApp') leftSwipeAt.current = null;
    switch (action.kind) {
      case 'pan':
        nudge(action.dx, action.dy);
        break;
      case 'scroll':
        send({ type: 'scroll', dy: Math.sign(action.dy) * swipeUnits(currentScrollLevel()) });
        setLastInput(`swipe → scroll ${action.dy < 0 ? 'up' : 'down'}`);
        break;
      case 'type':
        setMode('type');
        setLastInput('swipe → type');
        break;
      case 'nextApp': {
        const now = performance.now();
        if (!isSecondLeftSwipe(leftSwipeAt.current, now)) {
          leftSwipeAt.current = now;
          setLastInput('swipe left again to switch app');
          break;
        }
        leftSwipeAt.current = null;
        const next = nextAppSlot(currentApp.current, apps.length);
        if (next === null) setLastInput('no apps set up on the PC');
        else switchApp(next, false);
        break;
      }
    }
  };
  swipeRef.current = onSwipe;

  const togglePanSwipes = () => {
    const next = !live.current.panSwipes;
    live.current.panSwipes = next;
    setPanSwipes(next);
    setLastInput(next ? 'pan: swipes and edges move the view' : 'view locked; swipes: scroll, type, next app');
    // In Pointer mode, straight back to swiping so the new meaning takes effect at once.
    if (live.current.mode === 'pointer') setNav('view');
  };

  /**
   * Brings app N's window to the front, fitted to the cast area. From a button, Pointer is then
   * focused so going back is one pinch; from a swipe, focus stays where it is.
   */
  const switchApp = (slot: number, focusPointer = true) => {
    currentApp.current = slot;
    send({ type: 'switchApp', slot });
    setLastInput(`${apps[slot - 1] ?? `app ${slot}`}…`);
    if (focusPointer) focusModeButton('pointer');
  };

  const commitRegion = () => {
    if (draft) sendRegion(draft);
    setMode('pointer');
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
        // With Pan on, pushing past an edge pans the view. With Pan off the view stays locked, the
        // cursor goes up to the edge, and pushing on past the top or bottom scrolls instead.
        if (!live.current.panSwipes) {
          const from = c ?? centreOf(box);
          const { cursor: moved, overflowY } = moveCursorLocked(from, event.dx, event.dy, POINTER_GAIN, box);
          const height = stageRef.current?.clientHeight ?? 600;
          const py = lastPointer.current?.y;
          const pinned = py === undefined ? 0 : py >= height - 2 ? 1 : py <= 1 ? -1 : 0;
          const was = edgeScroll.current;
          const now = edgeScrollStep(was, { dy: event.dy, overflowY, pointerPinned: pinned });
          edgeScroll.current = now;

          if (now && !was) {
            // Start: the scrolling itself runs on the flush timer until the drag ends or comes
            // back in. Wait a little inside the edge, over the page rather than its border.
            const top = box.y + EDGE_SCROLL_INSET;
            const bottom = box.y + box.height - 1 - EDGE_SCROLL_INSET;
            const at = { x: moved.x, y: now.dir > 0 ? Math.min(moved.y, bottom) : Math.max(moved.y, top) };
            live.current.cursor = at;
            setCursor(at);
            queueMove(toNormalized(at, box));
            flushMove();
            setPanEdge({ x: 0, y: now.dir });
            setLastInput(`edge scroll ${now.dir > 0 ? 'down' : 'up'}`);
            return;
          }
          if (now) return; // scrolling: the cursor stays put
          if (was) stopEdgeScroll();

          live.current.cursor = moved;
          setCursor(moved);
          queueMove(toNormalized(moved, box));
          return;
        }

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
        stopEdgeScroll();
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
    lastPointer.current = p;
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
  const barHidden = nav === 'view' && VIEW_NAV_MODES.includes(mode);
  const shownScrollLevel = levelFor(scrollLevels, apps[activeApp - 1] ?? NO_APP);
  const audioState: AudioState = !audioOffered ? 'none' : !audioOn ? 'off' : audioBlocked ? 'blocked' : 'on';

  return (
    <div
      ref={stageRef}
      className={`stage look-${look}`}
      style={{ '--brightness': brightness } as React.CSSProperties}
    >
      <video ref={videoRef} autoPlay playsInline muted />
      {/* The PC's sound: kept playing (muted) so Web Audio gets the stream; see audioOutput.ts. */}
      <audio ref={audioRef} />
      <canvas ref={canvasRef} width={600} height={600} />
      <div
        className="gesture-layer"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerCancel}
        onMouseDown={(e) => e.preventDefault()}
      />

      <nav className={barHidden ? 'toolbar top dimmed' : 'toolbar top'} aria-label="Modes">
        {MODES.map(({ mode: m, label }) => (
          <button key={m} type="button" data-mode={m} aria-pressed={mode === m} onClick={() => setMode(m)}>
            {label}
          </button>
        ))}
        {apps.map((name, i) => (
          <button
            key={name + i}
            type="button"
            data-app={i + 1}
            aria-pressed={activeApp === i + 1}
            title={name}
            aria-label={`Switch to ${name}`}
            onClick={() => switchApp(i + 1)}
          >
            {i + 1}
          </button>
        ))}
        {/* Always shown so the bar doesn't shift between modes; it only changes Pointer-mode swipes. */}
        <button type="button" data-toggle="pan" aria-pressed={panSwipes} onClick={togglePanSwipes} title="Swipes and pushing past an edge pan the view">
          Pan
        </button>
        <button type="button" data-scroll={shownScrollLevel} onClick={cycleScrollLevel} title="Scroll strength for this app">
          ↕ {shownScrollLevel}
        </button>
        <button
          type="button"
          data-brightness={brightness}
          onClick={() => {
            const next = nextBrightness(brightness);
            setBrightness(next);
            saveBrightness(next);
            setLastInput(`brightness ${Math.round(next * 100)}%`);
          }}
          title="Video brightness"
        >
          ☀ {Math.round(brightness * 100)}%
        </button>
        {audioOffered && (
          // Always just ♪, so the bar never changes width: off loses the highlight, and a sound the
          // browser won't start before the next pinch or swipe shows in the warning colour.
          <button
            type="button"
            data-toggle="audio"
            data-output={outputState}
            data-blocked={audioState === 'blocked'}
            aria-pressed={audioOn}
            onClick={toggleAudio}
            title="The PC's sound"
          >
            ♪
          </button>
        )}
        <button type="button" data-look={look} onClick={() => setLook(nextLook)} title={`Display look: ${look}`}>
          Look
        </button>
        <button type="button" data-toggle="stats" aria-pressed={showStats} onClick={() => setShowStats((v) => !v)} title="Latency stats">
          Stats
        </button>
      </nav>

      {showStats && mode !== 'overview' && mode !== 'type' && (
        <div className={barHidden ? 'stats-panel' : 'stats-panel below-bar'} aria-label="Latency stats">
          {statsText.map((line, i) => (
            <div key={i}>{line}</div>
          ))}
        </div>
      )}

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
          <button type="button" onClick={() => setMode('pointer')}>
            Cancel
          </button>
        </nav>
      )}

      {mode === 'type' && (
        <TypePanel
          focusPinned={focusPinned}
          onSendText={(text) => {
            // Any length: in order, in pieces the PC accepts (500 characters each).
            for (const chunk of textChunks(text)) send({ type: 'typeText', text: chunk });
          }}
          onKey={(key: KeyName) => {
            send({ type: 'key', key });
            // Enter usually finishes the job: straight back to Pointer mode.
            if (key === 'Enter') setMode('pointer');
          }}
        />
      )}

      <footer className="status" aria-live="polite">
        <span className={mediaOk ? 'dot' : 'dot warn'}>●</span>
        <span>{mediaOk ? (status.path ? `live (${status.path})` : 'live') : status.media === 'waiting' ? 'starting video…' : status.media}</span>
        <span>{status.fps !== null ? `${status.fps.toFixed(0)} fps` : '– fps'}</span>
        <span>{status.rttMs !== null ? `${status.rttMs} ms` : '– ms'}</span>
        <span data-bandwidth>{bandwidthLabel(status.videoKbps, status.audioKbps, audioState)}</span>
        {/* H.264 is the norm; only a fallback is worth the room. */}
        <span>{status.codec && status.codec !== 'H264' ? status.codec : ''}</span>
        <span className="input-trace">{lastInput}</span>
        <span style={{ marginLeft: 'auto' }}>{mode}</span>
      </footer>
    </div>
  );
}

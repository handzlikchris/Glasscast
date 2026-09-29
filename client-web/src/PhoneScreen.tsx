// A phone session: the phone's screen (from its companion app, over WebRTC) with a cursor drawn
// here, and input sent straight to the phone on the DataChannel. A server (the PC today) only
// relays the setup (phoneConnect.ts): pairing and proving each other, the offer and the answer.
// Once the channel is open the relay closes, and the session doesn't need the server any more.
// Glasses and phone ping each other over the channel, and each ends the session when the other
// goes quiet (no server watches it).
//
// Controls, kept close to the PC session's:
// - On the view (pinches as on a laptop touchpad, as in a PC session): pinch-drag moves the
//   cursor; a pinch taps there, two quick ones double-tap; a pinch then a second pinch held
//   (tap-and-a-half) puts a finger down at the cursor, which follows the drag until release
//   (drag and drop, selecting; held still, a long press). Swipes (swipes.ts, shared with PC sessions):
//   up/down scroll around the cursor, left/right page (each after a 0.3 s wait for a second
//   swipe), right twice opens Type, left twice presses the phone's Back, down twice opens the app
//   overview (swipe left/right through it, pinch to pick; Fit follows the app you pick).
// - Back (middle-finger pinch) brings up the bar: Back · Home · Apps · Notif · Type · Region · Fit
//   · End. Swipe left/right along it, pinch to press; up/down or Back return to the view.
// - Type: the same panel and steps as a PC session (text box and composer → Send text → Enter);
//   the text goes to whatever has keyboard input on the phone. Enter is separate.
// - Region: the whole phone screen with a square box; drag moves it, swipe up/down zooms, a pinch
//   uses it, Back cancels. Fit: the phone crops to its top app window (a Samsung pop-up view
//   window made square) and follows it.
import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { DEFAULT_GESTURES, DOUBLE_TAP_MS, GestureTracker, HOLD_DRAG_MS, type GestureEvent } from './gestures';
import type { Point, Rect } from './geometry';
import {
  FULL_REGION,
  frameRect,
  moveRegion,
  phoneText,
  regionOnView,
  scrollSwipe,
  squareAround,
  toFrame,
  zoomRegion,
  describeBye,
  type FromPhone,
  type PhoneByeReason,
  type PhoneNav,
  type PhoneRegion,
  type ToPhone,
} from './phoneProtocol';
import { PhoneConnector } from './phoneConnect';
import { PhoneLink } from './phoneRtc';
import { openRelay, type PhoneState } from './phoneSignal';
import { usePinchPressesFocused } from './pinchPress';
import { PHONE_KEYS, TypePanel } from './TypePanel';
import type { Size } from './protocol';
import { PHONE_DOUBLES, SwipeReader, phoneSwipeAction, swipeOf, waitingHint, type SwipeGesture } from './swipes';

interface Props {
  /** Pair with the phone again even if a pairing is remembered. */
  pairAgain?: boolean;
  onEnded(reason: string): void;
  /** End on the bar: back to the PC/Phone choice. */
  onLeave(): void;
}

type Focus = 'view' | 'bar' | 'type' | 'region';

/** Region mode: the box being placed, and the crop to go back to on Back. */
interface Choosing {
  box: PhoneRegion;
  before: { region: PhoneRegion; follow: boolean };
}

/** Swipe up zooms in (a smaller box), down zooms out. */
const ZOOM_STEP = 1.25;

const PING_MS = 2000;
/** Nothing from the phone this long over the direct connection: it's gone (the phone uses 15 s). */
const SILENT_MS = 10_000;
/** Hidden this long (the app left), the session ends, as in a PC session. */
const HIDDEN_MS = 5000;
/**
 * After the Type panel moves focus to Send text or Enter, presses are ignored this long: the pinch
 * on the composer's Insert reached the page too and pressed Send text by itself (seen on the S25).
 */
const TYPE_GUARD_MS = 800;
/** How often a held finger's position goes to the phone while dragging (ms). */
const TOUCH_MOVE_MS = 40;
const POINTER_GAIN = 1.0;
const PHONE_STATUS: Record<PhoneState, string> = {
  offline: 'phone offline: open the companion app',
  ready: 'checking the phone…',
  asking: 'on the phone, tap Start to share',
  live: 'phone sharing',
};

const NAV_BUTTONS: { action: PhoneNav; label: string }[] = [
  { action: 'back', label: 'Back' },
  { action: 'home', label: 'Home' },
  { action: 'recents', label: 'Apps' },
  { action: 'notifications', label: 'Notif' },
];

export function PhoneScreen({ pairAgain = false, onEnded, onLeave }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const cursorRef = useRef<HTMLDivElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const phoneScreen = useRef<Size | null>(null);
  const phoneRegion = useRef<PhoneRegion>(FULL_REGION);
  const following = useRef(false);
  const choosing = useRef<Choosing | null>(null);
  const barRef = useRef<HTMLDivElement>(null);
  const linkRef = useRef<PhoneLink | null>(null);
  const connectorRef = useRef<PhoneConnector | null>(null);
  /** When the phone last said anything on the channel. */
  const lastHeard = useRef(0);
  /** Why the phone is ending the session, if it said so before closing. */
  const bye = useRef<PhoneByeReason | null>(null);
  const frame = useRef<Rect>({ x: 0, y: 0, width: 600, height: 600 });
  const cursor = useRef<Point>({ x: 300, y: 300 });
  // A pinch released before the hold (HOLD_DRAG_MS) is a tap; after it, the hold has taken over.
  const tracker = useRef(new GestureTracker(DEFAULT_GESTURES));
  const holdTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /** A tap waiting DOUBLE_TAP_MS for a second one (then it's a double tap). */
  const pendingTap = useRef<{ at: Point; timer: ReturnType<typeof setTimeout> } | null>(null);
  const touchSentAt = useRef(0);
  /** A finger is down on the phone (tap-and-a-half). */
  const touchDown = useRef(false);
  const swipeRef = useRef<(gesture: SwipeGesture) => void>(() => {});
  const swipes = useRef(
    new SwipeReader(
      (gesture) => swipeRef.current(gesture),
      (swipe) => setLastInput(waitingHint(swipe, 'phone')),
      undefined,
      PHONE_DOUBLES,
    ),
  );
  /** Text sent to the phone and not yet confirmed: it goes back in the box if the phone had no field. */
  const pendingText = useRef<string | null>(null);
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;
  const onLeaveRef = useRef(onLeave);
  onLeaveRef.current = onLeave;

  const [focus, setFocus] = useState<Focus>('view');
  const focusRef = useRef(focus);
  focusRef.current = focus;
  const [phone, setPhone] = useState('reaching the phone…');
  /** The pairing code, while the phone asks for approval. */
  const [code, setCode] = useState<string | null>(null);
  const [media, setMedia] = useState<RTCPeerConnectionState>('new');
  const [channelOpen, setChannelOpen] = useState(false);
  const [screen, setScreen] = useState<string | null>(null);
  const [path, setPath] = useState<'local' | 'remote' | null>(null);
  const [rttMs, setRttMs] = useState<number | null>(null);
  const [lastInput, setLastInput] = useState('');
  const [follow, setFollow] = useState(false);
  /** Text the phone had no field for, handed back to the Type panel. */
  const [refill, setRefill] = useState<{ text: string } | null>(null);

  // On the bar and in Type a pinch presses the focused button, wherever the glasses' pointer is.
  usePinchPressesFocused(focus === 'bar' || focus === 'type');

  const drawCursor = () => {
    const el = cursorRef.current;
    if (el) el.style.transform = `translate(${cursor.current.x}px, ${cursor.current.y}px)`;
  };

  const clampToFrame = (p: Point): Point => {
    const f = frame.current;
    return {
      x: Math.min(f.x + f.width - 1, Math.max(f.x, p.x)),
      y: Math.min(f.y + f.height - 1, Math.max(f.y, p.y)),
    };
  };

  const send = (message: ToPhone, label: string) => {
    const sent = linkRef.current?.send(message) ?? false;
    setLastInput(sent ? label : 'phone not connected yet');
  };

  const atCursor = () => toFrame(cursor.current, frame.current);

  /** The Region box over the whole screen's frame (which the phone sends while choosing). */
  const drawBox = () => {
    const el = boxRef.current;
    const choice = choosing.current;
    if (!el) return;
    if (!choice) {
      el.style.display = 'none';
      return;
    }
    const r = regionOnView(choice.box, frame.current);
    el.style.display = 'block';
    el.style.transform = `translate(${r.x}px, ${r.y}px)`;
    el.style.width = `${r.width}px`;
    el.style.height = `${r.height}px`;
  };

  // ---- connection ----
  useEffect(() => {
    let ended = false;
    const end = (reason: string) => {
      if (ended) return;
      ended = true;
      onEndedRef.current(reason);
    };

    let connector: PhoneConnector | null = null;
    const link = new PhoneLink((candidate) => connector?.sendCandidate(candidate), videoRef.current!, {
      onState: (state) => {
        setMedia(state);
        if (state === 'failed') end('The video connection to the phone failed.');
      },
      onChannel: (open) => {
        setChannelOpen(open);
        if (open) {
          lastHeard.current = performance.now();
          // Reached directly: the relay (and the server) aren't needed any more.
          connector?.connected();
        } else {
          end(bye.current ? describeBye(bye.current) : 'The connection to the phone closed.');
        }
      },
      onMessage: (message: FromPhone) => {
        lastHeard.current = performance.now();
        if (message.type === 'screen') {
          phoneScreen.current = { width: message.width, height: message.height };
          // While choosing, the phone shows the whole screen; the crop to keep is the box.
          if (!choosing.current) phoneRegion.current = message.region;
          following.current = message.follow;
          setFollow(message.follow);
          const w = Math.round(message.region.width * message.width);
          const h = Math.round(message.region.height * message.height);
          setScreen(message.follow ? `${message.app ?? 'window'} ${w}×${h}` : `${w}×${h}`);
        } else if (message.type === 'result') onResult(message.of, message.ok);
        else if (message.type === 'pong') setRttMs(Date.now() - message.t);
        else if (message.type === 'bye') bye.current = message.reason;
      },
    });
    linkRef.current = link;

    connector = new PhoneConnector(
      openRelay,
      {
        onPhone: (state) => setPhone(PHONE_STATUS[state]),
        onCode: setCode,
        onOffer: (sdp) => link.handleOffer(sdp),
        onCandidate: (candidate) => void link.addCandidate(candidate),
        onPong: (t) => setRttMs(Date.now() - t),
        onFailed: end,
      },
      pairAgain,
    );
    connectorRef.current = connector;

    // On the channel, the ping is how each side knows the other is still there (no server to say
    // so); before it opens, the relay's round trip.
    const ping = setInterval(() => {
      if (link.send({ type: 'ping', t: Date.now() })) {
        if (performance.now() - lastHeard.current > SILENT_MS) end('The phone stopped answering (out of range, or its app stopped).');
      } else {
        connector?.ping();
      }
    }, PING_MS);
    const pathPoll = setInterval(() => {
      link.path().then(setPath, () => {});
    }, 2000);
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

    return () => {
      ended = true;
      document.removeEventListener('visibilitychange', onVisibility);
      if (hiddenTimer !== null) clearTimeout(hiddenTimer);
      clearInterval(ping);
      clearInterval(pathPoll);
      // Closing the connection is what tells the phone (it ends its side when the channel closes).
      link.close();
      linkRef.current = null;
      connector?.close();
      connectorRef.current = null;
    };
  }, []);

  // The phone's frame changes size with its crop and rotation: re-fit it and keep the cursor on it.
  useEffect(() => {
    const video = videoRef.current!;
    const onResize = () => {
      if (!video.videoWidth || !video.videoHeight) return;
      frame.current = frameRect({ width: video.videoWidth, height: video.videoHeight });
      cursor.current = clampToFrame(cursor.current);
      drawCursor();
      drawBox();
    };
    video.addEventListener('resize', onResize);
    drawCursor();
    return () => video.removeEventListener('resize', onResize);
  }, []);

  // ---- region ----
  const startRegion = () => {
    const screenSize = phoneScreen.current;
    if (!screenSize) {
      setLastInput('phone not connected yet');
      return;
    }
    choosing.current = {
      box: squareAround(screenSize, phoneRegion.current),
      before: { region: phoneRegion.current, follow: following.current },
    };
    send({ type: 'setRegion', ...FULL_REGION }, 'choosing a region');
    setFocus('region');
    drawBox();
  };

  const finishRegion = (use: boolean) => {
    const choice = choosing.current;
    choosing.current = null;
    drawBox();
    setFocus('view');
    if (!choice) return;
    if (use) {
      phoneRegion.current = choice.box;
      send({ type: 'setRegion', ...choice.box }, 'region set');
    } else if (choice.before.follow) {
      send({ type: 'fitWindow' }, 'region cancelled');
    } else {
      send({ type: 'setRegion', ...choice.before.region }, 'region cancelled');
    }
  };

  const fitWindow = () => {
    send({ type: 'fitWindow' }, 'fit to window');
    setFocus('view');
  };

  // ---- focus: view, bar, type, region ----
  const back = () => {
    const current = focusRef.current;
    if (current === 'region') finishRegion(false);
    else setFocus(current === 'view' ? 'bar' : 'view');
  };
  const backRef = useRef(back);
  backRef.current = back;

  // Glasses Back calls history.back() only when there is an entry to go back to; keep one (SessionScreen does the same).
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

  useLayoutEffect(() => {
    if (focus === 'bar') {
      barRef.current?.querySelector('button')?.focus();
    } else if (focus !== 'type' && document.activeElement instanceof HTMLElement) {
      // (Type: the panel focuses and clicks its box itself, inside the pinch that opened it.)
      document.activeElement.blur();
    }
  }, [focus]);

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      const current = focusRef.current;
      if ((e.key === 'Escape' || e.key === 'Backspace') && !(e.target instanceof HTMLTextAreaElement)) {
        // The glasses' Back, as a key (as in a PC session).
        e.preventDefault();
        e.stopPropagation();
        backRef.current();
        return;
      }
      if (current === 'view') {
        const swipe = swipeOf(e.key);
        if (!swipe) return;
        e.preventDefault();
        e.stopPropagation();
        swipes.current.swipe(swipe);
      } else if (current === 'region') {
        const screenSize = phoneScreen.current;
        const choice = choosing.current;
        if (!screenSize || !choice || (e.key !== 'ArrowUp' && e.key !== 'ArrowDown')) return;
        e.preventDefault();
        choice.box = zoomRegion(screenSize, choice.box, e.key === 'ArrowUp' ? 1 / ZOOM_STEP : ZOOM_STEP);
        drawBox();
      } else if (current === 'bar') {
        const buttons = Array.from(barRef.current?.querySelectorAll('button') ?? []);
        const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
        if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
          e.preventDefault();
          const step = e.key === 'ArrowRight' ? 1 : -1;
          buttons[(Math.max(0, at) + step + buttons.length) % buttons.length]?.focus();
        } else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
          e.preventDefault();
          setFocus('view');
        }
      }
    };
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('keydown', onKeyDown, true);
      swipes.current.cancel();
    };
  }, []);

  swipeRef.current = (gesture: SwipeGesture) => {
    // A left/right that waited for a double may land after the swipes left the view.
    if (focusRef.current !== 'view') return;
    const action = phoneSwipeAction(gesture);
    if (action.kind === 'type') {
      setFocus('type');
      setLastInput('swipe right twice → type');
    } else if (action.kind === 'back') {
      send({ type: 'nav', action: 'back' }, 'swipe left twice → back');
    } else if (action.kind === 'apps') {
      send({ type: 'nav', action: 'recents' }, 'swipe down twice → apps: left/right, pinch to pick');
    } else {
      send(scrollSwipe(action.direction, atCursor()), `swipe ${action.direction}`);
    }
  };

  // ---- gestures on the view ----
  // A pinch taps at the cursor; a second within DOUBLE_TAP_MS makes it a double tap (so a single
  // tap goes that much later, as a click does in a PC session). That second pinch is armed: if it
  // moves, or stays down HOLD_DRAG_MS, the finger goes down at the cursor (no first tap), follows
  // the drag and lifts on release (drag and drop, selecting, a long press).
  const clearPendingTap = () => {
    if (pendingTap.current) clearTimeout(pendingTap.current.timer);
    pendingTap.current = null;
  };

  /** Sends a tap held back for a possible second one now (a hold or a drag followed it). */
  const flushTap = () => {
    const tap = pendingTap.current;
    if (!tap) return;
    clearPendingTap();
    send({ type: 'tap', ...tap.at }, 'tap');
  };

  const touch = (phase: 'down' | 'move' | 'up') => {
    if (phase === 'down' && touchDown.current) return;
    if (phase !== 'down' && !touchDown.current) return;
    touchDown.current = phase !== 'up';
    touchSentAt.current = performance.now();
    send({ type: 'touch', phase, ...atCursor() }, phase === 'down' ? 'finger down: move to drag' : phase === 'up' ? 'finger up' : 'drag');
  };

  const handleGesture = (event: GestureEvent) => {
    if (focusRef.current === 'region') {
      if (event.kind === 'drag' && choosing.current) {
        const f = frame.current;
        choosing.current.box = moveRegion(choosing.current.box, event.dx / f.width, event.dy / f.height);
        drawBox();
      } else if (event.kind === 'tap' || event.kind === 'holdEnd') {
        finishRegion(true);
      }
      return;
    }
    if (focusRef.current !== 'view') return;

    switch (event.kind) {
      case 'tap':
        if (pendingTap.current) {
          const at = pendingTap.current.at;
          clearPendingTap();
          send({ type: 'doubleTap', ...at }, 'double tap');
        } else {
          const at = atCursor();
          pendingTap.current = {
            at,
            timer: setTimeout(() => {
              pendingTap.current = null;
              send({ type: 'tap', ...at }, 'tap');
            }, DOUBLE_TAP_MS),
          };
        }
        break;
      case 'hold':
        clearPendingTap();
        touch('down');
        break;
      case 'dragStart':
        if (event.held) {
          clearPendingTap();
          touch('down');
        } else {
          flushTap();
        }
        break;
      case 'drag':
        cursor.current = clampToFrame({
          x: cursor.current.x + event.dx * POINTER_GAIN,
          y: cursor.current.y + event.dy * POINTER_GAIN,
        });
        drawCursor();
        // The finger follows, a few times a second is plenty (the phone smooths between).
        if (touchDown.current && performance.now() - touchSentAt.current >= TOUCH_MOVE_MS) touch('move');
        break;
      case 'dragEnd':
        if (event.held) touch('up');
        break;
      case 'holdEnd':
        touch('up');
        break;
    }
  };

  const clearHoldTimer = () => {
    if (holdTimer.current !== null) clearTimeout(holdTimer.current);
    holdTimer.current = null;
  };

  const onPointerDown = (e: ReactPointerEvent) => {
    const current = focusRef.current;
    if (current !== 'view' && current !== 'region') return;
    e.currentTarget.setPointerCapture(e.pointerId);
    // The second pinch of a quick pair (a tap is waiting): armed, tap-and-a-half.
    const armed = current === 'view' && pendingTap.current !== null;
    // Its first tap waits until this pinch is decided: a quick release makes a double tap, a drag
    // or a hold drops it.
    if (armed) clearTimeout(pendingTap.current!.timer);
    tracker.current.down(e.pointerId, e.clientX, e.clientY, e.timeStamp, armed);
    clearHoldTimer();
    if (!armed) return;
    const id = e.pointerId;
    holdTimer.current = setTimeout(() => {
      holdTimer.current = null;
      tracker.current.hold(id).forEach(handleGestureRef.current);
    }, HOLD_DRAG_MS);
  };

  const onPointerMove = (e: ReactPointerEvent) => {
    tracker.current.move(e.pointerId, e.clientX, e.clientY).forEach(handleGesture);
  };

  const onPointerUp = (e: ReactPointerEvent) => {
    clearHoldTimer();
    tracker.current.up(e.pointerId, e.clientX, e.clientY, e.timeStamp).forEach(handleGesture);
  };

  const onPointerCancel = (e: ReactPointerEvent) => {
    clearHoldTimer();
    tracker.current.cancel(e.pointerId).forEach(handleGesture);
    // A first tap paused for a second pinch that never finished goes out on its own.
    flushTap();
  };

  const handleGestureRef = useRef(handleGesture);
  handleGestureRef.current = handleGesture;

  // ---- bar and type ----
  const nav = (action: PhoneNav) => {
    send({ type: 'nav', action }, action);
    setFocus('view');
  };

  const sendText = (text: string) => {
    const pieces = phoneText(text);
    if (pieces.length > 0) pendingText.current = text;
    for (const piece of pieces) send({ type: 'typeText', text: piece }, 'text → phone');
  };

  const onResult = (of: 'typeText' | 'key' | 'switchApp', ok: boolean) => {
    if (of === 'switchApp') {
      // Only a failure is reported: the phone knows no other app yet (open one there first).
      if (!ok) setLastInput('no other app to switch to yet: open one on the phone first');
      return;
    }
    // Not ok: nothing on the phone takes keyboard input (an app like a remote desktop client only
    // does while its own keyboard is open) and there's no text field on screen either.
    if (of === 'key') {
      setLastInput(ok ? 'key sent' : "key not sent: open the phone app's keyboard (or tap a text field)");
      return;
    }
    const text = pendingText.current;
    pendingText.current = null;
    if (ok) {
      setLastInput('text typed on the phone');
      return;
    }
    // Nothing on the phone to type into: keep the text rather than lose it.
    setLastInput("not typed: open the phone app's keyboard (or tap a text field), then Send text");
    if (text) setRefill({ text });
  };

  /**
   * Moves focus for you and holds it briefly: the glasses reset focus (to the first bar button,
   * the phone's Back) when the composer closes. A swipe of yours in the meantime lets go.
   */
  const focusHeld = (el: HTMLElement) => {
    el.focus({ preventScroll: true });
    let swiped = false;
    const onKey = () => (swiped = true);
    document.addEventListener('keydown', onKey, true);
    for (const delay of [50, 150, 300, 500]) {
      setTimeout(() => {
        if (!swiped && el.isConnected && document.activeElement !== el) el.focus({ preventScroll: true });
      }, delay);
    }
    setTimeout(() => document.removeEventListener('keydown', onKey, true), 600);
  };

  const endSession = () => {
    linkRef.current?.send({ type: 'end' });
    onLeaveRef.current();
  };

  const connected = media === 'connected' && channelOpen;

  return (
    <div className={`stage look-lifted phone-stage${focus === 'region' ? ' choosing' : ''}`}>
      <video ref={videoRef} autoPlay playsInline muted />
      <div
        className="gesture-layer"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerCancel}
      />
      <div ref={cursorRef} className="phone-cursor" />
      <div ref={boxRef} className="phone-region" />

      <div ref={barRef} className={`toolbar top${focus === 'view' ? ' dimmed' : ''}`}>
        {NAV_BUTTONS.map(({ action, label }) => (
          <button key={action} type="button" onClick={() => nav(action)}>
            {label}
          </button>
        ))}
        <button type="button" aria-pressed={focus === 'type'} onClick={() => setFocus(focus === 'type' ? 'view' : 'type')}>
          Type
        </button>
        <button type="button" onClick={startRegion}>
          Region
        </button>
        <button type="button" aria-pressed={follow} onClick={fitWindow}>
          Fit
        </button>
        <button type="button" onClick={endSession}>
          End
        </button>
      </div>

      {focus === 'type' && (
        <TypePanel
          keys={PHONE_KEYS}
          placeholder="Speak or write, then Send text: it goes into the phone"
          focusPinned={focusHeld}
          onSendText={sendText}
          onKey={(key) => {
            send({ type: 'key', key }, key);
            // Enter usually finishes the job: straight back to the view.
            if (key === 'Enter') setFocus('view');
          }}
          refill={refill}
          guardMs={TYPE_GUARD_MS}
        />
      )}

      {code && (
        <div className="phone-pair" role="status">
          <p>Pair with the phone</p>
          <p className="code">{`${code.slice(0, 3)} ${code.slice(3)}`}</p>
          <p>Check the phone shows the same code, then tap Approve there.</p>
        </div>
      )}

      <div className="status">
        <span className={connected ? 'dot' : 'dot warn'}>●</span>
        <span>{connected ? `live (${path ?? '…'})` : code ? 'pairing: approve on the phone' : media === 'new' ? phone : media}</span>
        {screen && <span>{screen}</span>}
        {rttMs !== null && <span>{rttMs} ms</span>}
        <span className="input-trace">
          {focus === 'region' ? 'drag moves · swipe ↑↓ zooms · pinch uses · Back cancels' : lastInput}
        </span>
      </div>
    </div>
  );
}

// A phone session: the phone's screen (from its companion app, over WebRTC) with a cursor drawn
// here, and input sent straight to the phone on the DataChannel. The PC only relays signalling.
//
// Controls, kept close to the PC session's:
// - On the view: pinch-drag moves the cursor, a pinch taps there (any pinch shorter than a long
//   press), a longer pinch (held still) long-presses. Swipes (swipes.ts, shared with PC sessions):
//   up/down scroll around the cursor, left/right page (after a 0.5 s wait for a second swipe),
//   right twice opens Type, left twice presses the phone's Back.
// - Back (middle-finger pinch) brings up the bar: Back · Home · Apps · Notif · Type · Region · Fit
//   · End. Swipe left/right along it, pinch to press; up/down or Back return to the view.
// - Type: the composer's text goes into the phone's focused text field; Enter is separate.
// - Region: the whole phone screen with a square box; drag moves it, swipe up/down zooms, a pinch
//   uses it, Back cancels. Fit: the phone crops to its top app window (a Samsung pop-up view
//   window made square) and follows it.
import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { Session } from './connection';
import { DEFAULT_GESTURES, GestureTracker } from './gestures';
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
  type FromPhone,
  type PhoneNav,
  type PhoneRegion,
  type ToPhone,
} from './phoneProtocol';
import { PhoneLink } from './phoneRtc';
import { usePinchPressesFocused } from './pinchPress';
import type { PhoneState, ServerMessage, Size } from './protocol';
import { SwipeReader, phoneSwipeAction, swipeOf, waitingHint, type SwipeGesture } from './swipes';

interface Props {
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
/** Hidden this long (the app left), the session ends, as in a PC session. */
const HIDDEN_MS = 5000;
/** A press held this still and this long is a long press. */
const LONG_PRESS_MS = 600;
const POINTER_GAIN = 1.0;
/** Composer text that came as input events with no change event is sent after this quiet time. */
const INPUT_SETTLE_MS = 1500;

const PHONE_STATUS: Record<PhoneState, string> = {
  offline: 'phone offline: open the companion app',
  asking: 'on the phone, tap Start to share',
  live: 'phone sharing',
};

const NAV_BUTTONS: { action: PhoneNav; label: string }[] = [
  { action: 'back', label: 'Back' },
  { action: 'home', label: 'Home' },
  { action: 'recents', label: 'Apps' },
  { action: 'notifications', label: 'Notif' },
];

export function PhoneScreen({ onEnded, onLeave }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const cursorRef = useRef<HTMLDivElement>(null);
  const boxRef = useRef<HTMLDivElement>(null);
  const phoneScreen = useRef<Size | null>(null);
  const phoneRegion = useRef<PhoneRegion>(FULL_REGION);
  const following = useRef(false);
  const choosing = useRef<Choosing | null>(null);
  const barRef = useRef<HTMLDivElement>(null);
  const textRef = useRef<HTMLTextAreaElement>(null);
  const enterRef = useRef<HTMLButtonElement>(null);
  const linkRef = useRef<PhoneLink | null>(null);
  const sessionRef = useRef<Session | null>(null);
  const frame = useRef<Rect>({ x: 0, y: 0, width: 600, height: 600 });
  const cursor = useRef<Point>({ x: 300, y: 300 });
  // Any pinch that isn't a drag or a long press is a tap: no gap between the two where it's lost.
  const tracker = useRef(new GestureTracker({ ...DEFAULT_GESTURES, tapMaxMs: LONG_PRESS_MS }));
  const swipeRef = useRef<(gesture: SwipeGesture) => void>(() => {});
  const swipes = useRef(
    new SwipeReader(
      (gesture) => swipeRef.current(gesture),
      (swipe) => setLastInput(waitingHint(swipe, 'phone')),
    ),
  );
  /** Text sent to the phone and not yet confirmed: it goes back in the box if the phone had no field. */
  const pendingText = useRef<string | null>(null);
  const longPress = useRef<{ timer: ReturnType<typeof setTimeout>; fired: boolean } | null>(null);
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;
  const onLeaveRef = useRef(onLeave);
  onLeaveRef.current = onLeave;

  const [focus, setFocus] = useState<Focus>('view');
  const focusRef = useRef(focus);
  focusRef.current = focus;
  const [phone, setPhone] = useState('connecting to the PC…');
  const [media, setMedia] = useState<RTCPeerConnectionState>('new');
  const [channelOpen, setChannelOpen] = useState(false);
  const [screen, setScreen] = useState<string | null>(null);
  const [path, setPath] = useState<'local' | 'remote' | null>(null);
  const [rttMs, setRttMs] = useState<number | null>(null);
  const [lastInput, setLastInput] = useState('');
  const [follow, setFollow] = useState(false);

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

    let link: PhoneLink | null = null;
    const onMessage = (message: ServerMessage) => {
      switch (message.type) {
        case 'phoneStatus':
          setPhone(PHONE_STATUS[message.state]);
          break;
        case 'rtcOffer':
          link?.handleOffer(message.sdp).catch(() => end("The phone's video offer couldn't be used."));
          break;
        case 'iceCandidate':
          void link?.addCandidate({ candidate: message.candidate, sdpMid: message.sdpMid, sdpMLineIndex: message.sdpMLineIndex });
          break;
        case 'pong':
          setRttMs(Date.now() - message.t);
          break;
      }
    };

    let session: Session;
    try {
      session = Session.open({ onMessage, onClose: end }, 'phone');
    } catch {
      end('Not paired. Pair again.');
      return;
    }
    sessionRef.current = session;
    link = new PhoneLink(session.send.bind(session), videoRef.current!, {
      onState: (state) => {
        setMedia(state);
        if (state === 'failed') end('The video connection to the phone failed.');
      },
      onChannel: setChannelOpen,
      onMessage: (message: FromPhone) => {
        if (message.type === 'screen') {
          phoneScreen.current = { width: message.width, height: message.height };
          // While choosing, the phone shows the whole screen; the crop to keep is the box.
          if (!choosing.current) phoneRegion.current = message.region;
          following.current = message.follow;
          setFollow(message.follow);
          const w = Math.round(message.region.width * message.width);
          const h = Math.round(message.region.height * message.height);
          setScreen(message.follow ? `window ${w}×${h}` : `${w}×${h}`);
        }
        else if (message.type === 'result') onResult(message.of, message.ok);
      },
    });
    linkRef.current = link;

    session.send({ type: 'ping', t: Date.now() });
    const ping = setInterval(() => session.send({ type: 'ping', t: Date.now() }), PING_MS);
    const pathPoll = setInterval(() => {
      link?.path().then(setPath, () => {});
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
      link?.close();
      linkRef.current = null;
      session.close();
      sessionRef.current = null;
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
    } else if (focus === 'type') {
      const box = textRef.current!;
      box.focus();
      // Inside the pinch that opened Type, so the composer may open at once (as in a PC session).
      box.click();
    } else if (document.activeElement instanceof HTMLElement) {
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
    } else {
      send(scrollSwipe(action.direction, atCursor()), `swipe ${action.direction}`);
    }
  };

  // ---- gestures on the view ----
  const cancelLongPress = () => {
    if (longPress.current) clearTimeout(longPress.current.timer);
  };

  const onPointerDown = (e: ReactPointerEvent) => {
    const current = focusRef.current;
    if (current !== 'view' && current !== 'region') return;
    e.currentTarget.setPointerCapture(e.pointerId);
    tracker.current.down(e.pointerId, e.clientX, e.clientY, e.timeStamp);
    cancelLongPress();
    if (current === 'region') return;
    const state = { fired: false, timer: setTimeout(() => {
      state.fired = true;
      send({ type: 'longPress', ...atCursor() }, 'long press');
    }, LONG_PRESS_MS) };
    longPress.current = state;
  };

  const onPointerMove = (e: ReactPointerEvent) => {
    for (const event of tracker.current.move(e.pointerId, e.clientX, e.clientY)) {
      if (event.kind === 'dragStart') cancelLongPress();
      if (event.kind === 'drag' && choosing.current) {
        const f = frame.current;
        choosing.current.box = moveRegion(choosing.current.box, event.dx / f.width, event.dy / f.height);
        drawBox();
      } else if (event.kind === 'drag') {
        cursor.current = clampToFrame({
          x: cursor.current.x + event.dx * POINTER_GAIN,
          y: cursor.current.y + event.dy * POINTER_GAIN,
        });
        drawCursor();
      }
    }
  };

  const onPointerUp = (e: ReactPointerEvent) => {
    cancelLongPress();
    const fired = longPress.current?.fired ?? false;
    longPress.current = null;
    for (const event of tracker.current.up(e.pointerId, e.clientX, e.clientY, e.timeStamp)) {
      if (event.kind !== 'tap' || fired) continue;
      if (focusRef.current === 'region') finishRegion(true);
      else send({ type: 'tap', ...atCursor() }, 'tap');
    }
  };

  const onPointerCancel = (e: ReactPointerEvent) => {
    cancelLongPress();
    longPress.current = null;
    tracker.current.cancel(e.pointerId);
  };

  // ---- bar and type ----
  const nav = (action: PhoneNav) => {
    send({ type: 'nav', action }, action);
    setFocus('view');
  };

  const sendText = (text: string) => {
    const pieces = phoneText(text);
    if (pieces.length > 0) pendingText.current = text;
    for (const piece of pieces) send({ type: 'typeText', text: piece }, 'text → phone');
    return pieces.length > 0;
  };

  /** Sends what's in the box (the composer's text) and offers Enter. */
  const sendBox = () => {
    const box = textRef.current;
    if (!box || !sendText(box.value)) return;
    box.value = '';
    if (enterRef.current) focusHeld(enterRef.current);
  };

  const onResult = (of: 'typeText' | 'key', ok: boolean) => {
    if (of === 'key') {
      setLastInput(ok ? 'key sent' : 'key: no text field on the phone');
      return;
    }
    const text = pendingText.current;
    pendingText.current = null;
    if (ok) {
      setLastInput('text typed on the phone');
      return;
    }
    // Nothing on the phone to type into: keep the text rather than lose it.
    setLastInput('no text field on the phone: tap one, then Send text');
    const box = textRef.current;
    if (box && text && !box.value) box.value = text;
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

  // The composer hands its text back with input events and then, usually, a change event (the
  // native one, not React's onChange). Send on change; if only input came, send once it has been
  // quiet for INPUT_SETTLE_MS. Typing on a laptop keyboard counts the same way.
  const sendBoxRef = useRef(sendBox);
  sendBoxRef.current = sendBox;
  useEffect(() => {
    if (focus !== 'type') return;
    const box = textRef.current!;
    let settle: ReturnType<typeof setTimeout> | null = null;
    const onChange = () => {
      if (settle !== null) clearTimeout(settle);
      settle = null;
      sendBoxRef.current();
    };
    const onInput = () => {
      if (settle !== null) clearTimeout(settle);
      settle = setTimeout(onChange, INPUT_SETTLE_MS);
    };
    box.addEventListener('change', onChange);
    box.addEventListener('input', onInput);
    return () => {
      if (settle !== null) clearTimeout(settle);
      box.removeEventListener('change', onChange);
      box.removeEventListener('input', onInput);
    };
  }, [focus]);

  const endSession = () => {
    sessionRef.current?.close();
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
        <div className="type-panel">
          <textarea ref={textRef} rows={3} placeholder="Speak or write; it goes into the phone's text field" />
          <div className="row keys">
            <button type="button" onClick={sendBox}>
              Send text
            </button>
            <button ref={enterRef} type="button" onClick={() => { send({ type: 'key', key: 'Enter' }, 'Enter'); setFocus('view'); }}>
              Enter
            </button>
            <button type="button" onClick={() => send({ type: 'key', key: 'Backspace' }, '⌫')}>
              ⌫
            </button>
            <button type="button" onClick={() => setFocus('view')}>
              Done
            </button>
          </div>
        </div>
      )}

      <div className="status">
        <span className={connected ? 'dot' : 'dot warn'}>●</span>
        <span>{connected ? `live (${path ?? '…'})` : media === 'new' ? phone : media}</span>
        {screen && <span>{screen}</span>}
        {rttMs !== null && <span>{rttMs} ms</span>}
        <span className="input-trace">
          {focus === 'region' ? 'drag moves · swipe ↑↓ zooms · pinch uses · Back cancels' : lastInput}
        </span>
      </div>
    </div>
  );
}

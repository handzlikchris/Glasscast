// A phone session: the phone's screen (from its companion app, over WebRTC) with a cursor drawn
// here, and input sent straight to the phone on the DataChannel. The PC only relays signalling.
//
// Controls, kept close to the PC session's:
// - On the view: pinch-drag moves the cursor, a pinch taps there, a longer pinch (held still)
//   long-presses; swipes scroll (up/down) or page (left/right) around the cursor.
// - Back (middle-finger pinch) brings up the bar: Back · Home · Apps · Notif · Type · End. Swipe
//   left/right along it, pinch to press; up/down or Back return to the view.
// - Type: the composer's text goes into the phone's focused text field; Enter is separate.
import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { Session } from './connection';
import { GestureTracker } from './gestures';
import type { Point, Rect } from './geometry';
import { frameRect, phoneText, scrollSwipe, toFrame, type FromPhone, type PhoneNav, type SwipeDirection, type ToPhone } from './phoneProtocol';
import { PhoneLink } from './phoneRtc';
import { usePinchPressesFocused } from './pinchPress';
import type { PhoneState, ServerMessage } from './protocol';

interface Props {
  onEnded(reason: string): void;
}

type Focus = 'view' | 'bar' | 'type';

const PING_MS = 2000;
/** Hidden this long (the app left), the session ends, as in a PC session. */
const HIDDEN_MS = 5000;
/** A press held this still and this long is a long press. */
const LONG_PRESS_MS = 600;
const POINTER_GAIN = 1.0;

const PHONE_STATUS: Record<PhoneState, string> = {
  offline: 'phone offline: open the companion app',
  asking: 'on the phone, tap Start to share',
  live: 'phone sharing',
};

const SWIPES: Record<string, SwipeDirection> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
};

const NAV_BUTTONS: { action: PhoneNav; label: string }[] = [
  { action: 'back', label: 'Back' },
  { action: 'home', label: 'Home' },
  { action: 'recents', label: 'Apps' },
  { action: 'notifications', label: 'Notif' },
];

export function PhoneScreen({ onEnded }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const cursorRef = useRef<HTMLDivElement>(null);
  const barRef = useRef<HTMLDivElement>(null);
  const textRef = useRef<HTMLTextAreaElement>(null);
  const enterRef = useRef<HTMLButtonElement>(null);
  const linkRef = useRef<PhoneLink | null>(null);
  const sessionRef = useRef<Session | null>(null);
  const frame = useRef<Rect>({ x: 0, y: 0, width: 600, height: 600 });
  const cursor = useRef<Point>({ x: 300, y: 300 });
  const tracker = useRef(new GestureTracker());
  const longPress = useRef<{ timer: ReturnType<typeof setTimeout>; fired: boolean } | null>(null);
  const onEndedRef = useRef(onEnded);
  onEndedRef.current = onEnded;

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

  // Outside the view a pinch presses the focused button, wherever the glasses' pointer is.
  usePinchPressesFocused(focus !== 'view');

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
        if (message.type === 'screen') setScreen(`${message.width}×${message.height}`);
        else if (message.type === 'result') setLastInput(`${message.of === 'key' ? 'key' : 'text'} ${message.ok ? 'sent' : 'had no text field'}`);
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
    };
    video.addEventListener('resize', onResize);
    drawCursor();
    return () => video.removeEventListener('resize', onResize);
  }, []);

  // ---- focus: view, bar, type ----
  const back = () => {
    const current = focusRef.current;
    setFocus(current === 'view' ? 'bar' : 'view');
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
      if (current === 'view') {
        const direction = SWIPES[e.key];
        if (!direction) return;
        e.preventDefault();
        send(scrollSwipe(direction, atCursor()), `swipe ${direction}`);
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
    return () => document.removeEventListener('keydown', onKeyDown, true);
  }, []);

  // ---- gestures on the view ----
  const cancelLongPress = () => {
    if (longPress.current) clearTimeout(longPress.current.timer);
  };

  const onPointerDown = (e: ReactPointerEvent) => {
    if (focusRef.current !== 'view') return;
    e.currentTarget.setPointerCapture(e.pointerId);
    tracker.current.down(e.pointerId, e.clientX, e.clientY, e.timeStamp);
    cancelLongPress();
    const state = { fired: false, timer: setTimeout(() => {
      state.fired = true;
      send({ type: 'longPress', ...atCursor() }, 'long press');
    }, LONG_PRESS_MS) };
    longPress.current = state;
  };

  const onPointerMove = (e: ReactPointerEvent) => {
    for (const event of tracker.current.move(e.pointerId, e.clientX, e.clientY)) {
      if (event.kind === 'dragStart') cancelLongPress();
      if (event.kind === 'drag') {
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
      if (event.kind === 'tap' && !fired) send({ type: 'tap', ...atCursor() }, 'tap');
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
    for (const piece of pieces) send({ type: 'typeText', text: piece }, 'text');
    return pieces.length > 0;
  };

  // The composer hands its text back with a change event: send it and offer Enter.
  const onTextChange = () => {
    const box = textRef.current!;
    if (sendText(box.value)) {
      box.value = '';
      enterRef.current?.focus();
    }
  };

  // The native change event (not React's onChange, which is every keystroke): the composer closing.
  const onTextChangeRef = useRef(onTextChange);
  onTextChangeRef.current = onTextChange;
  useEffect(() => {
    if (focus !== 'type') return;
    const box = textRef.current!;
    const listener = () => onTextChangeRef.current();
    box.addEventListener('change', listener);
    return () => box.removeEventListener('change', listener);
  }, [focus]);

  const endSession = () => {
    sessionRef.current?.close();
    onEndedRef.current('You ended the phone session.');
  };

  const connected = media === 'connected' && channelOpen;

  return (
    <div className="stage look-lifted phone-stage">
      <video ref={videoRef} autoPlay playsInline muted />
      <div
        className="gesture-layer"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerCancel}
      />
      <div ref={cursorRef} className="phone-cursor" />

      <div ref={barRef} className={`toolbar top${focus === 'view' ? ' dimmed' : ''}`}>
        {NAV_BUTTONS.map(({ action, label }) => (
          <button key={action} type="button" onClick={() => nav(action)}>
            {label}
          </button>
        ))}
        <button type="button" aria-pressed={focus === 'type'} onClick={() => setFocus(focus === 'type' ? 'view' : 'type')}>
          Type
        </button>
        <button type="button" onClick={endSession}>
          End
        </button>
      </div>

      {focus === 'type' && (
        <div className="type-panel">
          <textarea ref={textRef} rows={3} placeholder="Speak or write; it goes into the phone's text field" />
          <div className="row keys">
            <button type="button" onClick={() => { const box = textRef.current!; if (sendText(box.value)) box.value = ''; }}>
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
        <span className="input-trace">{lastInput}</span>
      </div>
    </div>
  );
}

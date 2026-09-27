import { useRef, useState } from 'react';
import { isDeviceRemembered } from './connection';
import { PairingScreen } from './PairingScreen';
import { PhoneScreen } from './PhoneScreen';
import { usePinchPressesFocused } from './pinchPress';
import type { SessionTarget } from './protocol';
import { SessionScreen } from './SessionScreen';
import { loadTarget, saveTarget } from './target';

/** Clicks this soon after the choice appears are the end of the pinch that left a session. */
const CHOOSE_GUARD_MS = 400;

type Phase =
  | { kind: 'choose' }
  | { kind: 'pairing'; target: SessionTarget; attempt: number }
  | { kind: 'session'; target: SessionTarget; attempt: number }
  | { kind: 'ended'; target: SessionTarget; reason: string };

export function App() {
  // First the choice: this PC, or the phone through its companion app. The last one is focused,
  // so it's a single pinch after a restart.
  const [phase, setPhase] = useState<Phase>({ kind: 'choose' });
  // The pinch that pressed End also sends a click a moment later; it must not choose for you.
  const leftAt = useRef(-Infinity);
  usePinchPressesFocused(phase.kind !== 'session');

  // Remembered glasses (approved in the last 24 h) skip pairing; the PC gates both targets.
  const start = (target: SessionTarget) => {
    if (performance.now() - leftAt.current < CHOOSE_GUARD_MS) return;
    saveTarget(target);
    setPhase(
      isDeviceRemembered()
        ? { kind: 'session', target, attempt: Date.now() }
        : { kind: 'pairing', target, attempt: Date.now() },
    );
  };

  switch (phase.kind) {
    case 'choose': {
      const last = loadTarget();
      return (
        <main className="choose">
          <h1>Glasses Remote</h1>
          <div className="targets">
            <button type="button" autoFocus={last === 'pc'} onClick={() => start('pc')}>
              PC
            </button>
            <button type="button" autoFocus={last === 'phone'} onClick={() => start('phone')}>
              Phone
            </button>
          </div>
          <p>Phone needs its companion app running.</p>
          <p className="build">Build {__BUILD__}</p>
        </main>
      );
    }

    case 'pairing':
      return (
        <PairingScreen
          key={phase.attempt}
          onPaired={() => setPhase({ kind: 'session', target: phase.target, attempt: Date.now() })}
        />
      );

    case 'session': {
      const onEnded = (reason: string) => setPhase({ kind: 'ended', target: phase.target, reason });
      // End on the bar: straight back to the choice, not the ended screen (whose focused
      // Reconnect a pinch would press at once).
      const onLeave = () => {
        leftAt.current = performance.now();
        setPhase({ kind: 'choose' });
      };
      return phase.target === 'phone' ? (
        <PhoneScreen key={phase.attempt} onEnded={onEnded} onLeave={onLeave} />
      ) : (
        <SessionScreen key={phase.attempt} onEnded={onEnded} onLeave={onLeave} />
      );
    }

    case 'ended': {
      // Never automatic: reconnecting is your choice. Remembered glasses need no approval for it.
      const remembered = isDeviceRemembered();
      return (
        <main className="ended">
          <h1>Session ended</h1>
          <p>{phase.reason}</p>
          {remembered && (
            <button type="button" autoFocus onClick={() => setPhase({ kind: 'session', target: phase.target, attempt: Date.now() })}>
              Reconnect
            </button>
          )}
          <button type="button" autoFocus={!remembered} onClick={() => setPhase({ kind: 'pairing', target: phase.target, attempt: Date.now() })}>
            Pair again
          </button>
          <button type="button" onClick={() => setPhase({ kind: 'choose' })}>
            {phase.target === 'phone' ? 'PC or phone…' : 'Phone or PC…'}
          </button>
          <p className="build">Build {__BUILD__}</p>
        </main>
      );
    }
  }
}

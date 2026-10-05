import { useEffect, useRef, useState } from 'react';
import { isDeviceRemembered } from './connection';
import { loadFeatures, type Features } from './features';
import { forgetPairing, loadPairing } from './phoneTrust';
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
  | { kind: 'session'; target: SessionTarget; attempt: number; pairAgain?: boolean }
  | { kind: 'ended'; target: SessionTarget; reason: string };

export function App() {
  // First the choice: this PC, or the phone through its companion app. The last one is focused,
  // so it's a single pinch after a restart.
  const [phase, setPhase] = useState<Phase>({ kind: 'choose' });
  // A hosted relay offers the phone only; until the server says, no choice is shown.
  const [features, setFeatures] = useState<Features | null>(null);
  useEffect(() => {
    void loadFeatures().then(setFeatures);
  }, []);
  // The pinch that pressed End also sends a click a moment later; it must not choose for you.
  const leftAt = useRef(-Infinity);
  usePinchPressesFocused(phase.kind !== 'session');

  // PC: remembered glasses (approved in the last 24 h) skip pairing, which is approved on the PC.
  // Phone: straight in; the phone pairs the glasses itself (Approve on the phone) when needed.
  const start = (target: SessionTarget) => {
    if (performance.now() - leftAt.current < CHOOSE_GUARD_MS) return;
    saveTarget(target);
    setPhase(
      target === 'phone' || isDeviceRemembered()
        ? { kind: 'session', target, attempt: Date.now() }
        : { kind: 'pairing', target, attempt: Date.now() },
    );
  };

  switch (phase.kind) {
    case 'choose': {
      if (!features) {
        return (
          <main className="choose">
            <h1>Glasscast</h1>
            <p className="build">Build {__BUILD__}</p>
          </main>
        );
      }
      // Still one pinch after a restart: the only target, or the last one, is focused.
      const last = features.pc && features.phone ? loadTarget() : features.pc ? 'pc' : 'phone';
      return (
        <main className="choose">
          <h1>Glasscast</h1>
          <div className="targets">
            {features.pc && (
              <button type="button" autoFocus={last === 'pc'} onClick={() => start('pc')}>
                PC
              </button>
            )}
            {features.phone && (
              <button type="button" autoFocus={last === 'phone'} onClick={() => start('phone')}>
                Phone
              </button>
            )}
          </div>
          <p>
            {features.pc
              ? 'Phone needs its companion app running; it pairs on the phone.'
              : 'Needs the companion app running on your phone; it pairs on the phone. To control a PC, run Glasscast on it.'}
          </p>
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
        <PhoneScreen key={phase.attempt} pairAgain={phase.pairAgain} onEnded={onEnded} onLeave={onLeave} />
      ) : (
        <SessionScreen key={phase.attempt} onEnded={onEnded} onLeave={onLeave} />
      );
    }

    case 'ended': {
      // Never automatic: reconnecting is your choice. Remembered glasses need no approval for it.
      const phone = phase.target === 'phone';
      const remembered = phone ? loadPairing() !== null : isDeviceRemembered();
      const pairAgain = () => {
        if (phone) {
          forgetPairing();
          setPhase({ kind: 'session', target: 'phone', attempt: Date.now(), pairAgain: true });
        } else {
          setPhase({ kind: 'pairing', target: 'pc', attempt: Date.now() });
        }
      };
      return (
        <main className="ended">
          <h1>Session ended</h1>
          <p>{phase.reason}</p>
          {remembered && (
            <button type="button" autoFocus onClick={() => setPhase({ kind: 'session', target: phase.target, attempt: Date.now() })}>
              Reconnect
            </button>
          )}
          <button type="button" autoFocus={!remembered} onClick={pairAgain}>
            Pair again
          </button>
          <button type="button" onClick={() => setPhase({ kind: 'choose' })}>
            {features?.pc === false ? 'Start screen' : phase.target === 'phone' ? 'PC or phone…' : 'Phone or PC…'}
          </button>
          <p className="build">Build {__BUILD__}</p>
        </main>
      );
    }
  }
}

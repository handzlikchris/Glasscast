import { useState } from 'react';
import { isDeviceRemembered } from './connection';
import { PairingScreen } from './PairingScreen';
import { usePinchPressesFocused } from './pinchPress';
import { SessionScreen } from './SessionScreen';

type Phase = { kind: 'pairing'; attempt: number } | { kind: 'session'; attempt: number } | { kind: 'ended'; reason: string };

export function App() {
  // Remembered glasses (approved in the last 24 h) go straight into a session, e.g. after a restart.
  const [phase, setPhase] = useState<Phase>(() =>
    isDeviceRemembered() ? { kind: 'session', attempt: 0 } : { kind: 'pairing', attempt: 0 },
  );
  usePinchPressesFocused(phase.kind !== 'session');

  switch (phase.kind) {
    case 'pairing':
      return <PairingScreen key={phase.attempt} onPaired={() => setPhase({ kind: 'session', attempt: Date.now() })} />;

    case 'session':
      return <SessionScreen key={phase.attempt} onEnded={(reason) => setPhase({ kind: 'ended', reason })} />;

    case 'ended': {
      // Never automatic: reconnecting is your choice. Remembered glasses need no approval for it.
      const remembered = isDeviceRemembered();
      return (
        <main className="ended">
          <h1>Session ended</h1>
          <p>{phase.reason}</p>
          {remembered && (
            <button type="button" autoFocus onClick={() => setPhase({ kind: 'session', attempt: Date.now() })}>
              Reconnect
            </button>
          )}
          <button type="button" autoFocus={!remembered} onClick={() => setPhase({ kind: 'pairing', attempt: Date.now() })}>
            Pair again
          </button>
          <p className="build">Build {__BUILD__}</p>
        </main>
      );
    }
  }
}

import { useState } from 'react';
import { PairingScreen } from './PairingScreen';
import { SessionScreen } from './SessionScreen';

type Phase = { kind: 'pairing'; attempt: number } | { kind: 'session' } | { kind: 'ended'; reason: string };

export function App() {
  const [phase, setPhase] = useState<Phase>({ kind: 'pairing', attempt: 0 });

  switch (phase.kind) {
    case 'pairing':
      return <PairingScreen key={phase.attempt} onPaired={() => setPhase({ kind: 'session' })} />;

    case 'session':
      return <SessionScreen onEnded={(reason) => setPhase({ kind: 'ended', reason })} />;

    case 'ended':
      // No automatic reconnection: every session needs a fresh approval on the PC.
      return (
        <main className="ended">
          <h1>Session ended</h1>
          <p>{phase.reason}</p>
          <button type="button" autoFocus onClick={() => setPhase({ kind: 'pairing', attempt: Date.now() })}>
            Pair again
          </button>
          <p className="build">Build {__BUILD__}</p>
        </main>
      );
  }
}

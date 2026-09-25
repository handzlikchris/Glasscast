import { useEffect, useRef, useState } from 'react';
import { requestPairing } from './connection';

interface Props {
  onPaired(): void;
}

type State =
  | { kind: 'connecting' }
  | { kind: 'code'; code: string; expiresAt: number }
  | { kind: 'failed'; message: string };

export function PairingScreen({ onPaired }: Props) {
  const [state, setState] = useState<State>({ kind: 'connecting' });
  const [attempt, setAttempt] = useState(0);
  const [now, setNow] = useState(() => Date.now());
  // Keep the latest callback without restarting pairing when the parent re-renders.
  const onPairedRef = useRef(onPaired);
  onPairedRef.current = onPaired;

  useEffect(() => {
    const abort = new AbortController();
    setState({ kind: 'connecting' });

    requestPairing(
      {
        onCode: (code, expiresInSeconds) =>
          setState({ kind: 'code', code, expiresAt: Date.now() + expiresInSeconds * 1000 }),
      },
      abort.signal,
    ).then(
      () => onPairedRef.current(),
      (error: Error) => {
        if (!abort.signal.aborted) setState({ kind: 'failed', message: error.message });
      },
    );

    return () => abort.abort();
  }, [attempt]);

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 500);
    return () => clearInterval(timer);
  }, []);

  return (
    <main className="pairing">
      <h1>Glasses Remote</h1>
      {state.kind === 'connecting' && <p>Contacting your PC…</p>}
      {state.kind === 'code' && (
        <>
          <p>On the PC, approve the request showing this code:</p>
          <div className="code" aria-live="polite">
            {state.code}
          </div>
          <p>{Math.max(0, Math.ceil((state.expiresAt - now) / 1000))} s left</p>
        </>
      )}
      {state.kind === 'failed' && (
        <>
          <p className="error">{state.message}</p>
          <button type="button" autoFocus onClick={() => setAttempt((a) => a + 1)}>
            Try again
          </button>
        </>
      )}
      <p className="build">Build {__BUILD__}</p>
    </main>
  );
}

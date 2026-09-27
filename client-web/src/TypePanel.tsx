import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { PhoneKey } from './phoneProtocol';
import type { KeyName } from './protocol';

interface Props<K extends string> {
  /** Moves focus for the user and holds it against the glasses resetting it. */
  focusPinned(el: HTMLElement): void;
  onSendText(text: string): void;
  onKey(key: K): void;
  /** The key buttons under the text box; the one for 'Enter' gets focus after Send text. */
  keys: readonly { key: K; label: string }[];
  placeholder?: string;
  /** Text the target couldn't take, put back in the (empty) box; a new object each time. */
  refill?: { text: string } | null;
}

/** A PC session's keys. */
export const PC_KEYS: readonly { key: KeyName; label: string }[] = [
  { key: 'Enter', label: 'Enter' },
  { key: 'Escape', label: 'Esc' },
  { key: 'Tab', label: 'Tab' },
  { key: 'Backspace', label: '⌫' },
  { key: 'Ctrl+C', label: 'Ctrl+C' },
  { key: 'Ctrl+V', label: 'Ctrl+V' },
  { key: 'Alt+Tab', label: 'Alt+Tab' },
  { key: 'Win+Shift+Left', label: 'Win⇧←' },
  { key: 'Win+Shift+Right', label: 'Win⇧→' },
];

/** A phone session's keys (sent as a keyboard's key presses). */
export const PHONE_KEYS: readonly { key: PhoneKey; label: string }[] = [
  { key: 'Enter', label: 'Enter' },
  { key: 'Backspace', label: '⌫' },
];

/**
 * A plain textarea, so the glasses open their voice/handwriting composer.
 * Sending text never presses Enter: that's always a separate, deliberate tap,
 * so a misheard phrase can't run as a command.
 *
 * Focus is walked along for the glasses, so each step is just another pinch:
 * text box (clicked, to open the composer) when the panel opens → Send text once the composer hands text back
 * (a "change" event) → Enter after sending; Enter itself returns to the view (Pointer mode on the
 * PC). The same panel, and the same steps, in PC and phone sessions; only the keys differ.
 */
const NAV_KEYS = new Set(['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'Tab']);

export function TypePanel<K extends string>({ focusPinned, onSendText, onKey, keys, placeholder, refill }: Props<K>) {
  const [text, setText] = useState('');
  useEffect(() => {
    if (refill && !textRef.current) setText(refill.text);
  }, [refill]);
  const trimmed = text.trim();
  const textRef = useRef(text);
  textRef.current = text;
  const boxRef = useRef<HTMLTextAreaElement>(null);
  const sendRef = useRef<HTMLButtonElement>(null);
  const enterRef = useRef<HTMLButtonElement>(null);

  const focusPinnedRef = useRef(focusPinned);
  focusPinnedRef.current = focusPinned;

  // A layout effect, so it runs inside the pinch or swipe that opened Type (see setMode).
  useLayoutEffect(() => {
    const box = boxRef.current!;
    focusPinnedRef.current(box);
    // Click into the box so the composer opens straight away. The glasses only open it for your
    // own activation; if this doesn't count, one pinch on the box still does it.
    box.click();
    // When the composer closes the glasses may move focus elsewhere; only a swipe of yours
    // (or Tab) since the box got focus means you chose to go somewhere else.
    let movedByYou = false;
    const onFocusBox = () => (movedByYou = false);
    const onKeyDown = (e: KeyboardEvent) => {
      if (NAV_KEYS.has(e.key)) movedByYou = true;
    };
    const onChange = () => {
      // After the input events have re-rendered, so Send text is enabled.
      requestAnimationFrame(() => {
        if (!movedByYou && textRef.current.trim() && sendRef.current) focusPinnedRef.current(sendRef.current);
      });
    };
    box.addEventListener('focus', onFocusBox);
    box.addEventListener('change', onChange);
    document.addEventListener('keydown', onKeyDown, true);
    return () => {
      box.removeEventListener('focus', onFocusBox);
      box.removeEventListener('change', onChange);
      document.removeEventListener('keydown', onKeyDown, true);
    };
  }, []);

  const send = () => {
    if (!trimmed) return;
    onSendText(trimmed);
    setText('');
    if (enterRef.current) focusPinned(enterRef.current);
  };

  return (
    <section className="type-panel" aria-label="Type">
      <textarea
        ref={boxRef}
        value={text}
        // No length limit: long text goes to the PC in several typeText messages (textChunks).
        placeholder={placeholder ?? 'Speak or write, then Send text'}
        onChange={(e) => setText(e.target.value)}
        // Enter in the box inserts a newline (flattened to a space by the PC); it never submits.
        onPointerDown={(e) => e.stopPropagation()}
      />
      <div className="row">
        <button ref={sendRef} type="button" onClick={send} disabled={!trimmed}>
          Send text
        </button>
        <button type="button" onClick={() => setText('')} disabled={!text}>
          Clear
        </button>
        <span className="count">
          {text.length}
        </span>
      </div>
      <div className="row keys">
        {keys.map((s) => (
          <button key={s.key} ref={s.key === 'Enter' ? enterRef : undefined} type="button" onClick={() => onKey(s.key)}>
            {s.label}
          </button>
        ))}
      </div>
    </section>
  );
}

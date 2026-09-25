import { useEffect, useRef, useState } from 'react';
import { MAX_TEXT_LENGTH, type KeyName } from './protocol';

interface Props {
  onSendText(text: string): void;
  onKey(key: KeyName): void;
}

const SHORTCUTS: { key: KeyName; label: string }[] = [
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

/**
 * A plain textarea, so the glasses open their voice/handwriting composer.
 * Sending text never presses Enter: that's always a separate, deliberate tap,
 * so a misheard phrase can't run as a command.
 *
 * Focus is walked along for the glasses: the text box gets it when the panel opens,
 * and Send text gets it once the composer hands text back (a "change" event), so
 * each step is just another pinch.
 */
export function TypePanel({ onSendText, onKey }: Props) {
  const [text, setText] = useState('');
  const trimmed = text.trim();
  const textRef = useRef(text);
  textRef.current = text;
  const boxRef = useRef<HTMLTextAreaElement>(null);
  const sendRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const box = boxRef.current!;
    box.focus({ preventScroll: true });
    const onChange = () => {
      // After the input events have re-rendered, so Send text is enabled. Only if focus is
      // still in the box or went nowhere: if you moved to another control, that wins.
      requestAnimationFrame(() => {
        const active = document.activeElement;
        const free = active === box || active === null || active === document.body;
        if (free && textRef.current.trim()) sendRef.current?.focus({ preventScroll: true });
      });
    };
    box.addEventListener('change', onChange);
    return () => box.removeEventListener('change', onChange);
  }, []);

  const send = () => {
    if (!trimmed) return;
    onSendText(trimmed);
    setText('');
  };

  return (
    <section className="type-panel" aria-label="Type">
      <textarea
        ref={boxRef}
        value={text}
        maxLength={MAX_TEXT_LENGTH}
        placeholder="Speak or write, then Send text"
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
          {text.length}/{MAX_TEXT_LENGTH}
        </span>
      </div>
      <div className="row keys">
        {SHORTCUTS.map((s) => (
          <button key={s.key} type="button" onClick={() => onKey(s.key)}>
            {s.label}
          </button>
        ))}
      </div>
    </section>
  );
}

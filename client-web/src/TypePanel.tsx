import { useState } from 'react';
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
 */
export function TypePanel({ onSendText, onKey }: Props) {
  const [text, setText] = useState('');
  const trimmed = text.trim();

  const send = () => {
    if (!trimmed) return;
    onSendText(trimmed);
    setText('');
  };

  return (
    <section className="type-panel" aria-label="Type">
      <textarea
        value={text}
        maxLength={MAX_TEXT_LENGTH}
        placeholder="Speak or write, then Send text"
        onChange={(e) => setText(e.target.value)}
        // Enter in the box inserts a newline (flattened to a space by the PC); it never submits.
        onPointerDown={(e) => e.stopPropagation()}
      />
      <div className="row">
        <button type="button" onClick={send} disabled={!trimmed}>
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

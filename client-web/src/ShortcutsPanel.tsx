import { shortcutRows } from './shortcuts';

/** The ? panel: this session's gestures and swipe shortcuts, under the bar. */
export function ShortcutsPanel({ target }: { target: 'pc' | 'phone' }) {
  return (
    <div className="shortcuts-panel" role="note" aria-label="Shortcuts">
      {shortcutRows(target).map(({ gesture, action }) => (
        <div key={gesture} className="shortcut">
          <span>{gesture}</span>
          <span>{action}</span>
        </div>
      ))}
    </div>
  );
}

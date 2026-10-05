import { shortcutRows, type PhoneApp } from './shortcuts';

/** The ? panel: this session's gestures and swipe shortcuts, under the bar (in a phone app with a profile, its own first). */
export function ShortcutsPanel({ target, app }: { target: 'pc' | 'phone'; app?: PhoneApp }) {
  return (
    <div className="shortcuts-panel" role="note" aria-label="Shortcuts">
      {shortcutRows(target, app).map(({ gesture, action, heading }) =>
        heading ? (
          <div key={gesture} className="shortcut heading">
            <span>{gesture}</span>
          </div>
        ) : (
          <div key={gesture} className="shortcut">
            <span>{gesture}</span>
            <span>{action}</span>
          </div>
        ),
      )}
    </div>
  );
}

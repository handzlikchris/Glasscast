// On the glasses a pinch arrives as a pointer tap at the glasses' pointer position, not on the
// focused control, so on its own it only takes the focus away. Here a tap on anything but the
// focused button or text box presses that control instead: a button is clicked, a text box is
// focused and clicked inside the pinch, which is what opens the glasses' voice/handwriting
// composer. Used outside a session (first, pairing and ended screens) and on a phone session's
// bar and Type panel. SessionScreen has its own, richer handling for the same problem.
//
// A quick pinch can also arrive as an Enter key on the focused button, before or after the tap.
// Both are one pinch: a button pressed twice within SAME_PINCH_MS takes the first press only, or
// a toggle (the ? button) would switch on and straight back off.

import { useEffect } from 'react';
import { SAME_PINCH_MS } from './focusnav';

/** Presses of a button this close together are one pinch (its tap and its Enter). */
export class OnePressPerPinch {
  private last = -Infinity;

  /** A button is being pressed at `now` (ms): false if it's the same pinch as the last press. */
  press(now: number): boolean {
    if (now - this.last < SAME_PINCH_MS) return false;
    this.last = now;
    return true;
  }
}

export function usePinchPressesFocused(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    let pressing: number | null = null;
    let swallowClick = false;
    const presses = new OnePressPerPinch();

    const focusedControl = (): HTMLButtonElement | HTMLTextAreaElement | null => {
      const active = document.activeElement;
      if (active instanceof HTMLButtonElement && !active.disabled) return active;
      return active instanceof HTMLTextAreaElement ? active : null;
    };

    const onPointerDown = (e: PointerEvent) => {
      swallowClick = false;
      const control = focusedControl();
      if (!control || (e.target instanceof Node && control.contains(e.target))) return;
      // Keep the focus (and its ring) where it is; press it when the pinch ends.
      e.preventDefault();
      e.stopPropagation();
      pressing = e.pointerId;
    };
    const onPointerUp = (e: PointerEvent) => {
      if (e.pointerId !== pressing) return;
      pressing = null;
      e.stopPropagation();
      swallowClick = true;
      const control = focusedControl();
      if (control instanceof HTMLTextAreaElement) control.focus({ preventScroll: true });
      control?.click();
    };
    const onClick = (e: MouseEvent) => {
      if (swallowClick && e.isTrusted) {
        // The browser's own click for that pinch: the button has already been pressed.
        swallowClick = false;
        e.preventDefault();
        e.stopPropagation();
        return;
      }
      // Every press of a button ends up here: ours above, a tap on the button itself, an Enter.
      if (!(e.target instanceof Element) || !e.target.closest('button')) return;
      if (presses.press(performance.now())) return;
      e.preventDefault();
      e.stopPropagation();
    };

    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('pointerup', onPointerUp, true);
    document.addEventListener('click', onClick, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('pointerup', onPointerUp, true);
      document.removeEventListener('click', onClick, true);
    };
  }, [enabled]);
}

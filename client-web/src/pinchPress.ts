// On the glasses a pinch arrives as a pointer tap at the glasses' pointer position, not on the
// focused control, so on its own it only takes the focus away. Here a tap on anything but the
// focused button or text box presses that control instead: a button is clicked, a text box is
// focused and clicked inside the pinch, which is what opens the glasses' voice/handwriting
// composer. Used outside a session (first, pairing and ended screens) and on a phone session's
// bar and Type panel. SessionScreen has its own, richer handling for the same problem.

import { useEffect } from 'react';

export function usePinchPressesFocused(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    let pressing: number | null = null;
    let swallowClick = false;

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
      // The browser's own click for that pinch: the button has already been pressed.
      if (!swallowClick || !e.isTrusted) return;
      swallowClick = false;
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

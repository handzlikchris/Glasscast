// On the glasses a pinch arrives as a pointer tap at the glasses' pointer position, not on the
// focused button, so on its own it only takes the focus away. Outside a session (the pairing and
// "Session ended" screens) a tap on anything but the focused button presses that button instead.
// SessionScreen has its own, richer handling for the same problem.

import { useEffect } from 'react';

export function usePinchPressesFocused(enabled: boolean): void {
  useEffect(() => {
    if (!enabled) return;
    let pressing: number | null = null;
    let swallowClick = false;

    const focusedButton = (): HTMLButtonElement | null =>
      document.activeElement instanceof HTMLButtonElement && !document.activeElement.disabled
        ? document.activeElement
        : null;

    const onPointerDown = (e: PointerEvent) => {
      swallowClick = false;
      const button = focusedButton();
      if (!button || (e.target instanceof Node && button.contains(e.target))) return;
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
      focusedButton()?.click();
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

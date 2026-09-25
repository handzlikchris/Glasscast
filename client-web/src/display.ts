// Video brightness on the additive display, where white is the brightest (and most tiring)
// thing it can show. A multiplier on top of the display look; remembered on this device.
// Only this setting is stored: the session token never goes near storage (see connection.ts).

export const BRIGHTNESS_LEVELS = [1, 0.8, 0.65, 0.5] as const;
export type Brightness = (typeof BRIGHTNESS_LEVELS)[number];
export const DEFAULT_BRIGHTNESS: Brightness = 0.8;

const STORAGE_KEY = 'glasses.videoBrightness';

/** The next level down, wrapping from the dimmest back to full. */
export function nextBrightness(current: number): Brightness {
  const i = BRIGHTNESS_LEVELS.indexOf(current as Brightness);
  return BRIGHTNESS_LEVELS[(i + 1) % BRIGHTNESS_LEVELS.length];
}

/** A stored level, if it's one of ours; anything else (or no storage) gives the default. */
export function parseBrightness(stored: string | null): Brightness {
  const value = Number(stored);
  return BRIGHTNESS_LEVELS.find((level) => level === value) ?? DEFAULT_BRIGHTNESS;
}

export function loadBrightness(): Brightness {
  try {
    return parseBrightness(localStorage.getItem(STORAGE_KEY));
  } catch {
    return DEFAULT_BRIGHTNESS;
  }
}

export function saveBrightness(level: Brightness): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(level));
  } catch {
    // No storage (private mode, blocked): it just won't be remembered.
  }
}

// How hard scrolling scrolls, per app (Chrome wants less than a terminal), remembered on this
// device. One level sets both the swipe step and the edge-scroll speed.

/** Wheel notches one swipe scrolls by. */
export const SCROLL_LEVELS = [9, 5, 3, 2, 1] as const;
export type ScrollLevel = (typeof SCROLL_LEVELS)[number];
/** Three notches until the app is set otherwise (the user's pick on the glasses, 2026-09-27). */
export const DEFAULT_SCROLL_LEVEL: ScrollLevel = 3;

const NOTCH = 120;
const STORAGE_KEY = 'glasses.scrollLevels';
/** Levels are stored by app name; this is the key when the PC has no app shortcuts. */
export const NO_APP = '';

export type ScrollLevels = Record<string, ScrollLevel>;

/** The next level down, wrapping from the gentlest back to the strongest. */
export function nextScrollLevel(current: ScrollLevel): ScrollLevel {
  return SCROLL_LEVELS[(SCROLL_LEVELS.indexOf(current) + 1) % SCROLL_LEVELS.length];
}

export const levelFor = (levels: ScrollLevels, app: string): ScrollLevel => levels[app] ?? DEFAULT_SCROLL_LEVEL;

/** Wheel units for one swipe. */
export const swipeUnits = (level: ScrollLevel) => level * NOTCH;

/** Edge scrolling speed, wheel units per second: half a swipe's worth each second. */
export const edgeUnitsPerSecond = (level: ScrollLevel) => (level * NOTCH) / 2;

/** Stored levels, keeping only well-formed entries. */
export function parseScrollLevels(stored: string | null): ScrollLevels {
  const levels: ScrollLevels = {};
  try {
    const data: unknown = JSON.parse(stored ?? '{}');
    if (typeof data === 'object' && data !== null && !Array.isArray(data)) {
      for (const [app, level] of Object.entries(data)) {
        if (app.length <= 32 && SCROLL_LEVELS.includes(level as ScrollLevel)) levels[app] = level as ScrollLevel;
      }
    }
  } catch {
    // Unreadable: defaults.
  }
  return levels;
}

export function loadScrollLevels(): ScrollLevels {
  try {
    return parseScrollLevels(localStorage.getItem(STORAGE_KEY));
  } catch {
    return {};
  }
}

export function saveScrollLevels(levels: ScrollLevels): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(levels));
  } catch {
    // No storage: it just won't be remembered.
  }
}

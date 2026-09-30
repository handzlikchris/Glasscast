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

// ---- phone sessions ----
// A scroll there is a finger swipe on the phone, so its level is how long that swipe is, in
// percent of the first one (a third of the view in 220 ms). Kept apart from the PC's levels, by
// the name of the app the phone shows.

/** Percent of the first swipe's length (the phone's own fling makes the scroll go further). */
export const PHONE_SCROLL_LEVELS = [150, 100, 75, 50, 25] as const;
export type PhoneScrollLevel = (typeof PHONE_SCROLL_LEVELS)[number];
/** The first swipe scrolled too far in Claude's app; three quarters to start (2026-09-30). */
export const DEFAULT_PHONE_SCROLL_LEVEL: PhoneScrollLevel = 75;

const PHONE_STORAGE_KEY = 'glasses.phoneScrollLevels';

export type PhoneScrollLevels = Record<string, PhoneScrollLevel>;

/** The next level down, wrapping from the gentlest back to the strongest. */
export function nextPhoneScrollLevel(current: PhoneScrollLevel): PhoneScrollLevel {
  return PHONE_SCROLL_LEVELS[(PHONE_SCROLL_LEVELS.indexOf(current) + 1) % PHONE_SCROLL_LEVELS.length];
}

export const phoneLevelFor = (levels: PhoneScrollLevels, app: string): PhoneScrollLevel =>
  levels[app] ?? DEFAULT_PHONE_SCROLL_LEVEL;

/** Stored phone levels, keeping only well-formed entries. */
export function parsePhoneScrollLevels(stored: string | null): PhoneScrollLevels {
  const levels: PhoneScrollLevels = {};
  try {
    const data: unknown = JSON.parse(stored ?? '{}');
    if (typeof data === 'object' && data !== null && !Array.isArray(data)) {
      for (const [app, level] of Object.entries(data)) {
        if (app.length <= 32 && PHONE_SCROLL_LEVELS.includes(level as PhoneScrollLevel)) levels[app] = level as PhoneScrollLevel;
      }
    }
  } catch {
    // Unreadable: defaults.
  }
  return levels;
}

export function loadPhoneScrollLevels(): PhoneScrollLevels {
  try {
    return parsePhoneScrollLevels(localStorage.getItem(PHONE_STORAGE_KEY));
  } catch {
    return {};
  }
}

export function savePhoneScrollLevels(levels: PhoneScrollLevels): void {
  try {
    localStorage.setItem(PHONE_STORAGE_KEY, JSON.stringify(levels));
  } catch {
    // No storage: it just won't be remembered.
  }
}

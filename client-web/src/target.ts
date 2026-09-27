// The last target chosen on the first screen (PC or phone), so it's focused next time.
// Only a preference: kept in localStorage like the brightness and the ♪ setting.
import type { SessionTarget } from './protocol';

const TARGET_KEY = 'glassesRemote.target';

export function loadTarget(): SessionTarget {
  try {
    return localStorage.getItem(TARGET_KEY) === 'phone' ? 'phone' : 'pc';
  } catch {
    return 'pc';
  }
}

export function saveTarget(target: SessionTarget): void {
  try {
    localStorage.setItem(TARGET_KEY, target);
  } catch {
    // Blocked storage: PC is focused next time.
  }
}

// ✦ on the phone bar: an app's profile on or off, remembered per app (by package) on this device,
// like the ↕ scroll strength. An app's own profile starts on, Walk starts off; only a choice that
// differs from that is stored. Not a secret (CLAUDE.md lists what the client stores).

const STORAGE_KEY = 'glasses.appProfiles';

/** Package → on/off, only where it differs from the default. */
export type ProfileSwitches = Readonly<Record<string, boolean>>;

const PACKAGE = /^[A-Za-z0-9_.]{1,100}$/;

/** Stored switches, keeping only well-formed entries. */
export function parseProfileSwitches(stored: string | null): ProfileSwitches {
  const switches: Record<string, boolean> = {};
  try {
    const data: unknown = JSON.parse(stored ?? '{}');
    if (typeof data === 'object' && data !== null && !Array.isArray(data)) {
      for (const [pkg, on] of Object.entries(data)) {
        if (PACKAGE.test(pkg) && typeof on === 'boolean') switches[pkg] = on;
      }
    }
  } catch {
    // Unreadable: the defaults.
  }
  return switches;
}

export function loadProfileSwitches(): ProfileSwitches {
  try {
    return parseProfileSwitches(localStorage.getItem(STORAGE_KEY));
  } catch {
    return {};
  }
}

export function saveProfileSwitches(switches: ProfileSwitches): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(switches));
  } catch {
    // Storage unavailable: the choice lasts this session.
  }
}

/** Whether the profile is on for `pkg`: own profiles start on, Walk off. */
export const isOn = (switches: ProfileSwitches, pkg: string, ownProfile: boolean): boolean =>
  switches[pkg] ?? ownProfile;

/** The switches with `pkg` set to `on`, storing it only if that isn't the default. */
export function withSwitch(switches: ProfileSwitches, pkg: string, ownProfile: boolean, on: boolean): ProfileSwitches {
  const next: Record<string, boolean> = { ...switches };
  if (on === ownProfile) delete next[pkg];
  else next[pkg] = on;
  return next;
}

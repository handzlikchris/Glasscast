// The registry: which profile works for the app in front (architecture/app-profiles.md).
import { isOn, type ProfileSwitches } from './prefs';
import type { AppProfile } from './profile';
import { WALK } from './walk';

/** Every app's own profile. An app is in at most one. */
export const PROFILES: readonly AppProfile[] = [];

/** The app's own profile, if it has one. */
export function ownProfile(pkg: string, profiles: readonly AppProfile[] = PROFILES): AppProfile | null {
  return profiles.find((p) => p.packages.includes(pkg)) ?? null;
}

export interface ActiveProfile {
  /** What works now: the app's own profile or Walk, if on; null for the generic gestures. */
  profile: AppProfile | null;
  /** The app has a profile of its own (on or off). */
  own: boolean;
  /** ✦ is on for this app. */
  on: boolean;
}

/** The profile for the app in front (`pkg`; '' when the phone names none: nothing). */
export function activeProfile(pkg: string, switches: ProfileSwitches, profiles: readonly AppProfile[] = PROFILES): ActiveProfile {
  if (!pkg) return { profile: null, own: false, on: false };
  const own = ownProfile(pkg, profiles);
  const on = isOn(switches, pkg, own !== null);
  return { profile: on ? (own ?? WALK) : null, own: own !== null, on };
}

/**
 * The mark before the app's name on the status bar: ✦ while a profile works, ✧ while the app's
 * own one is off, nothing otherwise.
 */
export function profileMark(active: ActiveProfile): string {
  if (active.profile) return '✦';
  return active.own ? '✧' : '';
}

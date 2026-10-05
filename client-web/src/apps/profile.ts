// App profiles: gestures and behaviours made for one phone app, on top of the generic ones
// (architecture/app-profiles.md). This file is the shape of a profile and nothing app-specific;
// each app has its own file next to it, and index.ts picks the one for the app in front.
//
// The rules, in short: Back is never a profile's (it isn't a swipe, so a profile can't map it);
// a profile's gestures come first on the view and whatever it leaves out stays generic; pinches
// tap at the cursor as everywhere, a profile only decides where the cursor goes.
import type { PhoneItemKind, WalkUnit } from '../phoneProtocol';
import type { ShortcutRow } from '../shortcuts';
import type { Swipe, SwipeGesture } from '../swipes';

/** What a profile's gesture does. */
export type ProfileAction =
  /** Highlight a named control, found in the phone's `controls` list with the profile's matchers. */
  | { highlight: string }
  /** Ask the phone for the next or previous thing of a kind, in reading order. */
  | { walk: 'next' | 'previous'; unit: WalkUnit };

/** How a profile finds one of its named controls in the phone's list; a control's matchers are tried in order. */
export type ControlMatcher =
  /** The entry name of the view's resource id ("url_bar"). */
  | { id: string }
  /** Its label: content description, else text, else those inside it; a field's hint. */
  | { label: RegExp }
  /** By kind and place: the lowest or highest one of that kind on screen. */
  | { kind: PhoneItemKind; pick: 'lowest' | 'highest' }
  /** The control between two other named ones, on their row (a button with no label of its own). */
  | { between: readonly [string, string] };

export interface AppProfile {
  /** "Chrome": the ? panel's heading and the status bar. */
  name: string;
  /** The apps it's for, by package (display names change with the phone's language). */
  packages: readonly string[];
  /** View gestures it takes over; the rest stay generic. */
  gestures: Partial<Record<SwipeGesture, ProfileAction>>;
  /**
   * While something is highlighted: 'map' = single swipes step through `moves` at once (no doubles),
   * one the map doesn't list drops the highlight; 'view' = swipes act as on the view (a scroll drops
   * the highlight, a profile's double moves it again).
   */
  whileHighlighted: 'map' | 'view';
  /** Named controls and how to find them. */
  controls?: Readonly<Record<string, readonly ControlMatcher[]>>;
  /** What each named control is called on the glasses ("message box"); its name otherwise. */
  names?: Readonly<Record<string, string>>;
  /** From each named control, where a swipe goes: a list means "the first of these on screen". */
  moves?: Readonly<Record<string, Partial<Record<Swipe, string | readonly string[]>>>>;
  /** Said once on the status bar when the profile starts ("down twice for the next link"). */
  hint: string;
  /** Rows for the ? panel besides the gestures (which are derived from `gestures`). */
  notes: readonly ShortcutRow[];
}

/** A profile made from another, with some of its parts changed (Chrome is Walk plus headings). */
export function extend(base: AppProfile, changes: Partial<AppProfile>): AppProfile {
  return {
    ...base,
    ...changes,
    gestures: { ...base.gestures, ...changes.gestures },
    notes: changes.notes ?? base.notes,
  };
}

/** What a named control is called on the glasses. */
export const controlName = (profile: AppProfile, name: string): string => profile.names?.[name] ?? name;

const UNIT_NAMES: Record<WalkUnit, string> = {
  item: 'item',
  link: 'link',
  heading: 'heading',
  field: 'text field',
  article: 'post',
  landmark: 'section',
};

export const unitName = (unit: WalkUnit): string => UNIT_NAMES[unit];

/** A profile action in a few words, for the ? panel and the status bar. */
export function describeAction(profile: AppProfile, action: ProfileAction): string {
  if ('highlight' in action) return controlName(profile, action.highlight);
  return `${action.walk} ${unitName(action.unit)}`;
}

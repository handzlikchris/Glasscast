// The highlight: one thing on the phone's screen that a profile put the cursor on, with a box
// round it (architecture/app-profiles.md, "States of the view"). Pure; PhoneScreen holds it.
import type { PhoneItem, PhoneItemKind } from '../phoneProtocol';
import type { Swipe } from '../swipes';
import { controlName, describeAction, unitName, type AppProfile, type ProfileAction } from './profile';

export interface Highlight {
  item: PhoneItem;
  /** The named control it is (a map's), if it came from one. */
  name?: string;
}

/**
 * A single swipe while something is highlighted in a 'map' profile: the controls to try, first
 * on screen wins; null drops the highlight (nothing scrolls).
 */
export function mapMove(profile: AppProfile, highlight: Highlight, swipe: Swipe): readonly string[] | null {
  if (!highlight.name) return null;
  const to = profile.moves?.[highlight.name]?.[swipe];
  if (!to) return null;
  return typeof to === 'string' ? [to] : to;
}

/** After the tap on a highlighted thing: a text field opens Type (in every profile). */
export const opensType = (item: PhoneItem): boolean => item.kind === 'field';

const KIND_NAMES: Record<PhoneItemKind, string> = {
  button: 'button',
  link: 'link',
  field: 'text field',
  heading: 'heading',
  toggle: 'switch',
  text: 'item',
};

/** The status bar while something is highlighted: what it is, what a pinch does, where swipes go. */
export function highlightStatus(profile: AppProfile, highlight: Highlight): string {
  const what = highlight.name
    ? controlName(profile, highlight.name)
    : `${KIND_NAMES[highlight.item.kind]}${highlight.item.label ? ` '${highlight.item.label}'` : ''}`;
  const pinch = opensType(highlight.item) ? 'pinch to type' : 'pinch presses';
  if (profile.whileHighlighted === 'map' && highlight.name) {
    const moves = Object.entries(profile.moves?.[highlight.name] ?? {}).map(([swipe, to]) => {
      const first = typeof to === 'string' ? to : to[0];
      return `${swipe}: ${controlName(profile, first)}`;
    });
    return [`${profile.name} · ${what}`, pinch, ...moves].join(' · ');
  }
  return `${profile.name} · ${what} · ${pinch}`;
}

/** The status bar when a profile action finds nothing. */
export function notFoundStatus(profile: AppProfile, action: ProfileAction): string {
  if ('walk' in action) {
    return `${profile.name}: no more ${unitName(action.unit)}s ${action.walk === 'next' ? 'below' : 'above'}`;
  }
  return `${profile.name}: no ${describeAction(profile, action)} found (app updated?) · ✦ on the bar turns this profile off`;
}

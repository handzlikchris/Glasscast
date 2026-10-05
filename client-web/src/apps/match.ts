// Finding a profile's named controls in the phone's `controls` list (pure).
import type { PhoneItem } from '../phoneProtocol';
import type { AppProfile, ControlMatcher } from './profile';

const centreX = (i: PhoneItem) => i.x + i.w / 2;
const centreY = (i: PhoneItem) => i.y + i.h / 2;

/** The item a named control is on the phone's screen now, or null if it isn't there. */
export function findControl(profile: AppProfile, name: string, items: readonly PhoneItem[], depth = 0): PhoneItem | null {
  // `between` refers to other controls; a profile that loops would never end.
  if (depth > 4) return null;
  for (const matcher of profile.controls?.[name] ?? []) {
    const found = byMatcher(profile, matcher, items, depth);
    if (found) return found;
  }
  return null;
}

/** The first of `names` on screen, with its name: a move's list ("the mic, else Send"). */
export function firstOnScreen(
  profile: AppProfile,
  names: string | readonly string[],
  items: readonly PhoneItem[],
): { name: string; item: PhoneItem } | null {
  for (const name of typeof names === 'string' ? [names] : names) {
    const item = findControl(profile, name, items);
    if (item) return { name, item };
  }
  return null;
}

function byMatcher(profile: AppProfile, matcher: ControlMatcher, items: readonly PhoneItem[], depth: number): PhoneItem | null {
  if ('id' in matcher) return items.find((i) => i.id === matcher.id) ?? null;
  if ('label' in matcher) return items.find((i) => matcher.label.test(i.label)) ?? null;
  if ('pick' in matcher) {
    const ofKind = items.filter((i) => i.kind === matcher.kind);
    if (ofKind.length === 0) return null;
    return ofKind.reduce((best, i) =>
      matcher.pick === 'lowest' ? (i.y + i.h > best.y + best.h ? i : best) : i.y < best.y ? i : best,
    );
  }
  const [leftName, rightName] = matcher.between;
  const left = findControl(profile, leftName, items, depth + 1);
  const right = findControl(profile, rightName, items, depth + 1);
  if (!left || !right) return null;
  const top = Math.min(left.y, right.y);
  const bottom = Math.max(left.y + left.h, right.y + right.h);
  const [from, to] = [centreX(left), centreX(right)].sort((a, b) => a - b);
  const between = items.filter(
    (i) => i !== left && i !== right && centreX(i) > from && centreX(i) < to && centreY(i) > top && centreY(i) < bottom,
  );
  if (between.length === 0) return null;
  return between.reduce((best, i) => (Math.abs(centreX(i) - centreX(left)) < Math.abs(centreX(best) - centreX(left)) ? i : best));
}

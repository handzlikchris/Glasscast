// The ? panel on the bar: how the glasses' gestures work in this session, for when you forget.
// The swipe shortcuts (doubles) come from swipes.ts itself, so they can't drift; the pinch and
// Back rows are text here: change them with the gestures (CLAUDE.md, "Glasses controls").
// In a phone session with an app profile (apps/), the app's gestures come first under its name,
// then the generic ones it leaves alone.
import { describeAction, type AppProfile } from './apps/profile';
import { pcSwipeAction, phoneSwipeAction, type SwipeGesture } from './swipes';

export interface ShortcutRow {
  gesture: string;
  action: string;
  /** A heading over the rows that follow ("In Chrome"), not a gesture. */
  heading?: boolean;
}

/** The app in front of a phone session, for the ? panel. */
export interface PhoneApp {
  /** The profile at work (the app's own, or Walk), if any. */
  profile: AppProfile | null;
  /** The app's own profile is switched off with ✦: its name, to say so. */
  offName?: string;
}

const DOUBLES: { gesture: SwipeGesture; label: string }[] = [
  { gesture: 'doubleRight', label: 'swipe right ×2' },
  { gesture: 'doubleLeft', label: 'swipe left ×2' },
  { gesture: 'doubleDown', label: 'swipe down ×2' },
  { gesture: 'doubleUp', label: 'swipe up ×2' },
];

function pcDouble(gesture: SwipeGesture): string | null {
  switch (pcSwipeAction(gesture, 'pointer', false).kind) {
    case 'type':
      return 'Type';
    case 'nextApp':
      return 'next app';
    default:
      return null;
  }
}

function phoneDouble(gesture: SwipeGesture, profile: AppProfile | null): string | null {
  switch (phoneSwipeAction(gesture, profile).kind) {
    case 'type':
      return 'Type';
    case 'apps':
      return 'app overview';
    case 'back':
      return 'Back';
    default:
      return null;
  }
}

/** The rows of the ? panel for a PC or phone session. */
export function shortcutRows(target: 'pc' | 'phone', app: PhoneApp = { profile: null }): ShortcutRow[] {
  const profile = target === 'phone' ? app.profile : null;
  const doubles = DOUBLES.flatMap(({ gesture, label }) => {
    const action = target === 'pc' ? pcDouble(gesture) : phoneDouble(gesture, profile);
    return action ? [{ gesture: label, action }] : [];
  });
  const click = target === 'pc' ? 'click' : 'tap';
  const generic: ShortcutRow[] = [
    ...doubles,
    target === 'pc'
      ? { gesture: 'swipe up / down', action: 'scroll (Pan: move the view)' }
      : { gesture: 'swipe up / down', action: 'scroll' },
    target === 'pc'
      ? { gesture: 'swipe left / right', action: 'Pan on: move the view' }
      : { gesture: 'swipe left / right', action: 'page sideways' },
    { gesture: 'pinch', action: click },
    { gesture: 'two quick pinches', action: `double ${click}` },
    { gesture: 'pinch, hold 0.2 s, move', action: 'move the cursor' },
    { gesture: 'pinch, then pinch and move', action: target === 'pc' ? 'drag (button held)' : 'drag (finger held)' },
    { gesture: 'Back (middle pinch)', action: target === 'pc' ? 'the bar · from the bar: Pointer' : 'the bar (Apps first) · again: the view' },
    ...(target === 'phone'
      ? [
          { gesture: 'in the app overview', action: 'left / right move, down: the row of apps (up: back), pinch picks, Back leaves' },
          { gesture: '✦ on the bar', action: "this app's profile on / off (any app: Walk)" },
        ]
      : []),
  ];
  if (target !== 'phone') return generic;
  if (profile) {
    const own = DOUBLES.flatMap(({ gesture, label }) => {
      const action = profile.gestures[gesture];
      return action ? [{ gesture: label, action: describeAction(profile, action) }] : [];
    });
    return [
      { gesture: `In ${profile.name}`, action: '', heading: true },
      ...own,
      ...profile.notes,
      { gesture: 'Everywhere', action: '', heading: true },
      ...generic,
    ];
  }
  if (app.offName) return [{ gesture: `${app.offName} profile off (✦ turns it on)`, action: '', heading: true }, ...generic];
  return generic;
}

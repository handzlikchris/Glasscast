// The ? panel on the bar: how the glasses' gestures work in this session, for when you forget.
// The swipe shortcuts (doubles) come from swipes.ts itself, so they can't drift; the pinch and
// Back rows are text here: change them with the gestures (CLAUDE.md, "Glasses controls").
import { pcSwipeAction, phoneSwipeAction, type SwipeGesture } from './swipes';

export interface ShortcutRow {
  gesture: string;
  action: string;
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

function phoneDouble(gesture: SwipeGesture): string | null {
  switch (phoneSwipeAction(gesture).kind) {
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
export function shortcutRows(target: 'pc' | 'phone'): ShortcutRow[] {
  const doubles = DOUBLES.flatMap(({ gesture, label }) => {
    const action = target === 'pc' ? pcDouble(gesture) : phoneDouble(gesture);
    return action ? [{ gesture: label, action }] : [];
  });
  const click = target === 'pc' ? 'click' : 'tap';
  return [
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
    { gesture: 'Back (middle pinch)', action: target === 'pc' ? 'the bar · from the bar: Pointer' : 'the bar · again: the view' },
    ...(target === 'phone' ? [{ gesture: 'in the app overview', action: 'left / right move, down: the row of apps (up: back), pinch picks, Back leaves' }] : []),
  ];
}

// Walk: the built-in profile for any app without one of its own. Down twice and up twice step
// to the next and previous thing you can press, in reading order (the phone walks the screen's
// tree); up and down still scroll, a pinch presses the highlighted thing. Off until ✦ turns it
// on for an app: it takes down twice, which is the phone's Back without a profile.
import type { AppProfile } from './profile';

export const WALK: AppProfile = {
  name: 'Walk',
  packages: [],
  gestures: {
    doubleDown: { walk: 'next', unit: 'item' },
    doubleUp: { walk: 'previous', unit: 'item' },
  },
  whileHighlighted: 'view',
  hint: 'down twice: the next thing to press, up twice: the one before',
  notes: [{ gesture: 'pinch (highlighted)', action: 'press it; a text field opens Type' }],
};

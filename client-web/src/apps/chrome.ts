// Chrome: Walk plus headings (architecture/app-profiles.md, "Chrome: walking the page"). Up and
// down still scroll; the four doubles hop through the page in reading order, the phone scrolling
// each hop into view. Chrome hands the whole page to the companion (links, headings, fields from
// its HTML and ARIA roles), so nothing here knows any one site.
import { extend } from './profile';
import { WALK } from './walk';

export const CHROME = extend(WALK, {
  name: 'Chrome',
  packages: ['com.android.chrome', 'com.chrome.beta', 'com.chrome.dev', 'com.chrome.canary'],
  gestures: {
    // Bigger hops, section to section. To try on the device: 'article' (a Reddit post) or 'landmark'.
    doubleRight: { walk: 'next', unit: 'heading' },
    doubleLeft: { walk: 'previous', unit: 'heading' },
  },
  hint: 'down/up twice: next/previous link or button, right/left twice: next/previous heading',
  notes: [
    { gesture: 'pinch (highlighted)', action: 'press it; a text field opens Type' },
    { gesture: 'Type', action: 'pinch a text field, or Type on the bar' },
    { gesture: 'app overview', action: 'Back, then pinch (Apps)' },
  ],
});

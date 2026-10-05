import { describe, expect, it } from 'vitest';
import type { PhoneItem } from '../phoneProtocol';
import { highlightStatus, mapMove, notFoundStatus, opensType } from './highlight';
import type { AppProfile } from './profile';
import { WALK } from './walk';

const item = (kind: PhoneItem['kind'], label = ''): PhoneItem => ({ x: 0, y: 0, w: 0.1, h: 0.1, kind, label, id: '' });

const mapped: AppProfile = {
  name: 'Chat',
  packages: ['com.example.chat'],
  gestures: { doubleDown: { highlight: 'box' } },
  whileHighlighted: 'map',
  controls: {},
  names: { box: 'message box', attach: '+', mic: 'microphone' },
  moves: { box: { down: 'attach' }, attach: { up: 'box', right: ['mic', 'send'] } },
  hint: '',
  notes: [],
};

describe('the highlight', () => {
  it("a map's swipes go where it says, else drop the highlight", () => {
    expect(mapMove(mapped, { item: item('field'), name: 'box' }, 'down')).toEqual(['attach']);
    expect(mapMove(mapped, { item: item('button'), name: 'attach' }, 'right')).toEqual(['mic', 'send']);
    expect(mapMove(mapped, { item: item('field'), name: 'box' }, 'up')).toBeNull();
    // A walked item has no name: nothing to step from.
    expect(mapMove(mapped, { item: item('link') }, 'down')).toBeNull();
  });

  it('a text field opens Type after its tap; nothing else does', () => {
    expect(opensType(item('field'))).toBe(true);
    expect(opensType(item('button'))).toBe(false);
    expect(opensType(item('link'))).toBe(false);
  });

  it('says what is highlighted and what comes next', () => {
    expect(highlightStatus(mapped, { item: item('field'), name: 'box' })).toBe('Chat · message box · pinch to type · down: +');
    expect(highlightStatus(mapped, { item: item('button'), name: 'attach' })).toBe('Chat · + · pinch presses · up: message box · right: microphone');
    expect(highlightStatus(WALK, { item: item('link', 'Comments') })).toBe("Walk · link 'Comments' · pinch presses");
  });

  it('says when there is nothing to go to', () => {
    expect(notFoundStatus(WALK, { walk: 'next', unit: 'item' })).toBe('Walk: no more items below');
    expect(notFoundStatus(WALK, { walk: 'previous', unit: 'heading' })).toBe('Walk: no more headings above');
    expect(notFoundStatus(mapped, { highlight: 'box' })).toBe('Chat: no message box found (app updated?) · ✦ on the bar turns this profile off');
  });
});

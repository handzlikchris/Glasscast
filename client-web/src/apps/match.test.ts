import { describe, expect, it } from 'vitest';
import type { PhoneItem } from '../phoneProtocol';
import claudeScreen from './fixtures/claude-code-session.json';
import { findControl, firstOnScreen } from './match';
import type { AppProfile } from './profile';

// Claude's controls as the companion lists them (from a UI dump on the S25, 2026-10-05).
const items = claudeScreen.items as PhoneItem[];

const profile: AppProfile = {
  name: 'Test',
  packages: ['com.example'],
  gestures: {},
  whileHighlighted: 'map',
  hint: '',
  notes: [],
  controls: {
    box: [{ kind: 'field', pick: 'lowest' }],
    attach: [{ label: /^Add context$/ }],
    mic: [{ label: /^Start speech input$/ }],
    model: [{ between: ['attach', 'mic'] }],
    menu: [{ label: /^Open menu/ }],
    gone: [{ label: /^Nowhere$/ }],
    // The first matcher misses, the second finds it.
    send: [{ id: 'send_button' }, { label: /^Send$/ }],
    loop: [{ between: ['loop', 'mic'] }],
  },
};

const labelOf = (name: string) => findControl(profile, name, items)?.label;

describe('findControl', () => {
  it('finds controls by label, kind and place', () => {
    expect(findControl(profile, 'box', items)?.kind).toBe('field');
    expect(labelOf('attach')).toBe('Add context');
    expect(labelOf('menu')).toBe('Open menu, new feature available');
  });

  it('tries the matchers in order', () => {
    expect(labelOf('send')).toBe('Send');
    expect(findControl(profile, 'send', [...items, { ...items[0], id: 'send_button', label: 'other' }])?.label).toBe('other');
  });

  it("finds a button with no label of its own between two that have one (Claude's model picker)", () => {
    expect(labelOf('model')).toBe('Opus 5.5 High');
    // With the microphone gone, there's nothing to be between.
    expect(findControl(profile, 'model', items.filter((i) => i.label !== 'Start speech input'))).toBeNull();
  });

  it("is null for a control that isn't there, an unknown name or a loop", () => {
    expect(findControl(profile, 'gone', items)).toBeNull();
    expect(findControl(profile, 'nothing', items)).toBeNull();
    expect(findControl(profile, 'loop', items)).toBeNull();
  });

  it('takes the first of a list that is on screen', () => {
    expect(firstOnScreen(profile, ['gone', 'mic', 'send'], items)?.name).toBe('mic');
    expect(firstOnScreen(profile, 'send', items)?.item.label).toBe('Send');
    expect(firstOnScreen(profile, ['gone'], items)).toBeNull();
  });
});

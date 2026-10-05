import { describe, expect, it } from 'vitest';
import type { PhoneItem } from '../phoneProtocol';
import { shortcutRows } from '../shortcuts';
import { phoneSwipeAction } from '../swipes';
import { CLAUDE } from './claude';
import claudeScreen from './fixtures/claude-code-session.json';
import { highlightStatus } from './highlight';
import { activeProfile } from './index';
import { findControl, firstOnScreen } from './match';

// Claude's controls as the companion lists them (from a UI dump on the S25, 2026-10-05).
const items = claudeScreen.items as PhoneItem[];
const labelOf = (name: string) => findControl(CLAUDE, name, items)?.label;

describe('the Claude profile', () => {
  it("is Claude's own, on by default", () => {
    expect(activeProfile('com.anthropic.claude', {}).profile).toBe(CLAUDE);
  });

  it('finds every control on the S25 screen', () => {
    expect(findControl(CLAUDE, 'box', items)?.kind).toBe('field');
    expect(labelOf('attach')).toBe('Add context');
    expect(labelOf('model')).toBe('Opus 5.5 High');
    expect(labelOf('mic')).toBe('Start speech input');
    expect(labelOf('send')).toBe('Send');
  });

  it('every move lands on a control it knows, and the row reads left to right', () => {
    const names = Object.keys(CLAUDE.controls!);
    for (const [from, moves] of Object.entries(CLAUDE.moves!)) {
      expect(names).toContain(from);
      for (const to of Object.values(moves)) for (const name of typeof to === 'string' ? [to] : to!) expect(names).toContain(name);
    }
    const right = (from: string) => firstOnScreen(CLAUDE, CLAUDE.moves![from].right!, items)?.name;
    expect([right('attach'), right('model'), right('mic')]).toEqual(['model', 'mic', 'send']);
    // Without the microphone (Claude may swap it), the model picker is between + and send, and right skips on.
    const noMic = items.filter((i) => i.label !== 'Start speech input');
    expect(findControl(CLAUDE, 'model', noMic)?.label).toBe('Opus 5.5 High');
    expect(firstOnScreen(CLAUDE, CLAUDE.moves!.model.right!, noMic)?.name).toBe('send');
  });

  it('takes only down twice: Type and the app overview stay generic', () => {
    expect(phoneSwipeAction('doubleDown', CLAUDE)).toEqual({ kind: 'profile', action: { highlight: 'box' } });
    expect(phoneSwipeAction('doubleRight', CLAUDE)).toEqual({ kind: 'type' });
    expect(phoneSwipeAction('doubleLeft', CLAUDE)).toEqual({ kind: 'apps' });
    expect(phoneSwipeAction('doubleUp', CLAUDE)).toEqual({ kind: 'swipe', direction: 'up' });
  });

  it('says where you are and where the swipes go', () => {
    const box = findControl(CLAUDE, 'box', items)!;
    expect(highlightStatus(CLAUDE, { item: box, name: 'box' })).toBe('Claude · message box · pinch to type · down: +');
    const rows = shortcutRows('phone', { profile: CLAUDE });
    expect(rows.find((r) => r.gesture === 'swipe down ×2')?.action).toBe('message box');
    expect(rows.find((r) => r.gesture === 'swipe right ×2')?.action).toBe('Type');
  });
});

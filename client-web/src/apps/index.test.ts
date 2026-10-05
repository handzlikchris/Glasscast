import { describe, expect, it } from 'vitest';
import { activeProfile, profileMark } from './index';
import { parseProfileSwitches, withSwitch } from './prefs';
import type { AppProfile } from './profile';
import { WALK } from './walk';

const chromeLike: AppProfile = { ...WALK, name: 'Browser', packages: ['com.example.browser'] };
const profiles = [chromeLike];

describe('the active profile', () => {
  it("is the app's own profile, on by default", () => {
    const active = activeProfile('com.example.browser', {}, profiles);
    expect(active).toEqual({ profile: chromeLike, own: true, on: true });
    expect(profileMark(active)).toBe('✦');
  });

  it('is Walk for any other app, off by default', () => {
    const off = activeProfile('com.example.notes', {}, profiles);
    expect(off).toEqual({ profile: null, own: false, on: false });
    expect(profileMark(off)).toBe('');
    const on = activeProfile('com.example.notes', { 'com.example.notes': true }, profiles);
    expect(on.profile).toBe(WALK);
    expect(profileMark(on)).toBe('✦');
  });

  it('is nothing for an own profile switched off, marked hollow', () => {
    const active = activeProfile('com.example.browser', { 'com.example.browser': false }, profiles);
    expect(active).toEqual({ profile: null, own: true, on: false });
    expect(profileMark(active)).toBe('✧');
  });

  it('is nothing when the phone names no app', () => {
    expect(activeProfile('', { '': true } as never, profiles).profile).toBeNull();
  });
});

describe('✦ switches', () => {
  it('store only what differs from the default', () => {
    let s = withSwitch({}, 'com.example.browser', true, false);
    expect(s).toEqual({ 'com.example.browser': false });
    s = withSwitch(s, 'com.example.browser', true, true);
    expect(s).toEqual({});
    s = withSwitch(s, 'com.example.notes', false, true);
    expect(s).toEqual({ 'com.example.notes': true });
  });

  it('read back only well-formed entries', () => {
    expect(parseProfileSwitches('{"com.a":false,"com.b":"yes","bad key":true,"com.c":true}')).toEqual({ 'com.a': false, 'com.c': true });
    expect(parseProfileSwitches('[1]')).toEqual({});
    expect(parseProfileSwitches('not json')).toEqual({});
    expect(parseProfileSwitches(null)).toEqual({});
  });
});

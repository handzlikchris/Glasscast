// Drives the real glasses client (client-web/dist) in headless Chrome against the
// e2e harness (tools/e2e-harness), which runs the real server with pairing
// auto-approved and input recorded instead of injected.
//
//   npm run drive -- [--headed] [--screenshots <dir>]
//
// Exit code 0 when every step passed.
import puppeteer from 'puppeteer-core';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const BASE = 'http://127.0.0.1:5081';
const args = process.argv.slice(2);
const headed = args.includes('--headed');
const shotsIndex = args.indexOf('--screenshots');
const shots = shotsIndex >= 0 ? args[shotsIndex + 1] : null;
if (shots) mkdirSync(shots, { recursive: true });

const chromePath = process.env.CHROME_PATH ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';
const results = [];
const check = (name, ok, detail = '') => {
  results.push({ name, ok, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  (${detail})` : ''}`);
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const input = async () => (await fetch(`${BASE}/__harness/input`)).json();

const browser = await puppeteer.launch({
  executablePath: chromePath,
  headless: !headed,
  args: ['--autoplay-policy=no-user-gesture-required', '--window-size=620,700'],
});

try {
  const page = await browser.newPage();
  await page.setViewport({ width: 600, height: 600 });
  page.on('pageerror', (err) => console.log(`[page error] ${err.message}`));
  const shot = async (name) => shots && page.screenshot({ path: join(shots, `${name}.png`) });
  // The mode bar is hidden (and ignores taps) while swipes are on the view, as on the glasses,
  // so plain navigation presses its buttons directly.
  const tapBar = (selector) => page.$eval(`.toolbar.top ${selector}`, (el) => el.click());

  // 1. Pairing (auto-approved by the harness) leads straight into a session.
  await page.goto(`${BASE}/`, { waitUntil: 'load' });
  await page.waitForSelector('.pairing .code', { timeout: 10_000 }).catch(() => {});
  await shot('1-pairing');
  const session = await page.waitForSelector('.stage', { timeout: 15_000 }).then(() => true, () => false);
  check('pairing leads into a session', session);

  // 2. Video arrives over WebRTC.
  const live = await page
    .waitForFunction(() => document.querySelector('.status')?.textContent?.includes('live'), { timeout: 15_000 })
    .then(() => true, () => false);
  await sleep(3000);
  const statusText = await page.$eval('.status', (el) => el.textContent);
  const fps = Number(/(\d+) fps/.exec(statusText)?.[1] ?? 0);
  check('video is live', live && fps > 0, statusText.replace(/\s+/g, ' '));
  const playing = await page.$eval('video', (v) => v.videoWidth > 0 && !v.paused);
  check('video element is playing 600px frames', playing);
  const lookLabel = await page.$eval('button[data-look]', (el) => el.dataset.look);
  // Brightness: starts at 80% (a fresh browser has nothing stored); one press steps down to 65%.
  const videoFilter = () => page.$eval('video', (v) => getComputedStyle(v).filter);
  const filterBefore = await videoFilter();
  await page.$eval('button[data-brightness]', (el) => el.click());
  await sleep(100);
  const brightnessAfter = await page.$eval('button[data-brightness]', (el) => el.textContent.trim());
  const filterAfter = await videoFilter();
  await page.$eval('button[data-brightness]', (el) => el.click()); // 50%
  await page.$eval('button[data-brightness]', (el) => el.click()); // 100%
  await page.$eval('button[data-brightness]', (el) => el.click()); // back to 80%
  check('the brightness button dims the video', filterBefore !== filterAfter && brightnessAfter.endsWith('65%'),
    `${filterBefore} -> ${filterAfter} (${brightnessAfter})`);
  check('a session starts in Pointer mode with the lifted look',
    statusText.trim().endsWith('pointer') && lookLabel === 'lifted', `${lookLabel}`);
  await shot('2-view');

  // 3. Pointer mode: drag moves, short tap clicks.
  await tapBar('button[data-mode="pointer"]');
  await page.mouse.move(300, 300);
  await page.mouse.down();
  await page.mouse.move(360, 330, { steps: 10 });
  await page.mouse.up();
  await sleep(200);
  await page.mouse.click(360, 330);
  await sleep(800); // clicks wait briefly in case a second pinch follows
  let actions = await input();
  check('pointer drag moves the cursor', actions.some((a) => a.startsWith('move ')));
  check('short tap clicks', actions.includes('click Left'));
  await shot('3-pointer');

  // 4. Scroll mode: dragging up scrolls down (negative Windows wheel).
  await tapBar('button[data-mode="scroll"]');
  await page.mouse.move(300, 400);
  await page.mouse.down();
  await page.mouse.move(300, 280, { steps: 12 });
  await page.mouse.up();
  await sleep(300);
  actions = await input();
  check('scroll drag sends wheel', actions.some((a) => /^wheel -\d+/.test(a)));

  // 5. Type mode: text and keys are separate; text never presses Enter.
  await tapBar('button[data-mode="type"]');
  await page.locator('textarea').fill('hello from e2e\nsecond line');
  await page.locator('button::-p-text(Send text)').click();
  await sleep(200);
  actions = await input();
  check('typed text arrives flattened, without Enter', actions.includes('type hello from e2e second line') && !actions.includes('key Enter'));
  await page.locator('button::-p-text(Enter)').click();
  await sleep(200);
  actions = await input();
  check('Enter is its own deliberate key press', actions.includes('key Enter'));
  await shot('4-type');

  // 6. Region (overview): drag the region box and commit it.
  await tapBar('button[data-mode="overview"]');
  await sleep(500);
  await page.mouse.move(300, 300);
  await page.mouse.down();
  await page.mouse.move(250, 280, { steps: 8 });
  await page.mouse.up();
  await shot('5-overview');
  await page.locator('button::-p-text(Use region)').click();
  await sleep(500);
  const mode = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  check('Use region returns to Pointer mode', mode === 'pointer', mode);

  // 7. Edge panning: in Pointer mode, pushing past the right edge slides the region right.
  const before = await (await fetch(`${BASE}/__harness/region`)).json();
  await tapBar('button[data-mode="pointer"]');
  await page.mouse.move(60, 300);
  await page.mouse.down();
  await page.mouse.move(590, 300, { steps: 40 });
  await page.mouse.up();
  await sleep(600);
  const after = await (await fetch(`${BASE}/__harness/region`)).json();
  await shot('6-edge-pan');
  check('pushing past the right edge pans the region right', after && before && after.x > before.x && after.y === before.y,
    `x ${before?.x} -> ${after?.x}`);
  await tapBar('button[data-mode="scroll"]');
  await sleep(200);

  // 8. Taps don't click in Scroll mode (nothing new recorded).
  const inputsBefore = (await input()).length;
  await page.mouse.click(300, 300);
  await sleep(300);
  check('Scroll mode ignores taps', (await input()).length === inputsBefore);

  // 9. On the controls, a pinch (a tap on the video) presses the focused button. Tab
  //    stands in for the glasses' swipes. Picking Scroll sends swipes back to the view.
  await page.focus('button[data-mode="overview"]');
  let focusedMode = null;
  for (let i = 0; i < 8 && focusedMode !== 'scroll'; i++) {
    await page.keyboard.press('Tab');
    focusedMode = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  }
  await page.mouse.click(300, 300);
  await sleep(300);
  const pinchStatus = await page.$eval('.status', (el) => el.textContent);
  const focusAfterPick = await page.evaluate(() => document.activeElement?.tagName ?? null);
  check('a pinch presses the focused button, then swipes go back to the view',
    pinchStatus.includes('scroll') && focusAfterPick === 'BODY', `focus ${focusAfterPick}`);

  // 10. Swipes move the view by a quarter of its size (left here: earlier steps panned right).
  const regionAt = async () => (await fetch(`${BASE}/__harness/region`)).json();
  const beforeSwipe = await regionAt();
  await page.keyboard.press('ArrowLeft');
  await sleep(400);
  const afterSwipe = await regionAt();
  check('a left swipe moves the view left by a quarter of its width',
    afterSwipe.x === Math.max(0, beforeSwipe.x - Math.round(beforeSwipe.width / 4)) && afterSwipe.y === beforeSwipe.y,
    `x ${beforeSwipe.x} -> ${afterSwipe.x}, width ${beforeSwipe.width}`);

  // Pinch, then pinch and hold (a tap, then a press held still past the hold time).
  // The hand wobbles during the hold (40 px here); that must not count as a drag.
  const pinchThenHold = async () => {
    await page.mouse.click(300, 300);
    await sleep(100);
    await page.mouse.down();
    await page.mouse.move(340, 320, { steps: 5 });
    await sleep(700);
    await page.mouse.up();
    await sleep(200);
  };

  // 11. Pinch, then pinch-hold puts focus on the likely next mode (Pointer, from Scroll);
  //     swipes then stay on the controls.
  await pinchThenHold();
  const heldFocus = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  const beforeControlsSwipe = await regionAt();
  await page.keyboard.press('ArrowRight');
  await sleep(300);
  const afterControlsSwipe = await regionAt();
  check('pinch, then pinch-hold from Scroll focuses Pointer', heldFocus === 'pointer' &&
    afterControlsSwipe.x === beforeControlsSwipe.x, `focus ${heldFocus}`);

  // 12. Pointer mode: a pinch clicks after a short wait, two quick pinches double-click,
  //     and pinch-then-hold clicks nothing.
  await page.keyboard.press('ArrowLeft'); // back onto Pointer after the swipe above
  await page.mouse.click(300, 300);
  await sleep(300);
  const clicksIn = async (fn) => {
    const before = (await input()).length;
    await fn();
    await sleep(700);
    return (await input()).slice(before).filter((a) => a === 'click Left').length;
  };
  const single = await clicksIn(() => page.mouse.click(320, 300));
  const double = await clicksIn(async () => {
    await page.mouse.click(320, 300);
    await sleep(100);
    await page.mouse.click(320, 300);
  });
  const beforeHold = (await input()).length;
  const held = await clicksIn(pinchThenHold);
  const heldMoves = (await input()).slice(beforeHold).filter((a) => a.startsWith('move ')).length;
  const heldToType = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  check('in Pointer mode a pinch clicks once and two quick pinches double-click', single === 1 && double === 2,
    `single ${single}, double ${double}`);
  check('in Pointer mode a wobbly pinch-then-hold focuses Type without clicking or moving',
    held === 0 && heldMoves === 0 && heldToType === 'type', `clicks ${held}, moves ${heldMoves}, focus ${heldToType}`);

  // 13. The whole Type round trip by pinches alone (taps on the video): Type → text box →
  //     (composer) → Send text → Pointer focused → Pointer.
  const activeName = () => page.evaluate(() => {
    const el = document.activeElement;
    if (!el || el === document.body) return 'BODY';
    return el.dataset?.mode ?? (el.textContent?.trim() || el.tagName);
  });
  const pinch = async () => {
    await page.mouse.click(300, 150);
    await sleep(400);
  };
  await pinch(); // presses Type (focused by step 12's pinch-then-hold)
  const afterType = await page.evaluate(() => document.activeElement?.tagName ?? null);
  await page.keyboard.type('from the composer');
  await sleep(700); // past the hold on the text box
  // Like the glasses when the composer closes: focus jumps to the first button, which blurs the
  // box and fires its change event. Focus must still end up on Send text.
  await page.focus('button[data-mode="overview"]');
  await sleep(300);
  const afterComposer = await activeName();
  const typedBefore = (await input()).length;
  await pinch(); // presses Send text
  const afterSend = await activeName();
  await pinch(); // presses Enter, which returns to Pointer mode
  const typed = (await input()).slice(typedBefore);
  const roundTripMode = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  const afterEnter = roundTripMode;
  check('Type round trip by pinches: text box, Send text, Enter, then Pointer mode',
    afterType === 'TEXTAREA' && afterComposer === 'Send text' && afterSend === 'Enter' &&
      typed.join('|') === 'type from the composer|key Enter' && roundTripMode === 'pointer',
    `after Type ${afterType}, composer ${afterComposer}, Send ${afterSend}, Enter ${afterEnter}, sent ${typed.join('|')}`);

  // 14. Back out of Type mode: focus the Pointer button with the keyboard, then "pinch" on the
  //     text box (where the glasses' pointer tends to be after typing). Pointer gets pressed.
  await tapBar('button[data-mode="type"]');
  await page.locator('textarea').fill('typed on the glasses');
  await page.keyboard.press('ArrowRight'); // a swipe of yours: leaving the box won't jump to Send text
  await sleep(700); // past the hold on the text box
  await page.focus('button[data-mode="type"]');
  await page.keyboard.down('Shift');
  await page.keyboard.press('Tab'); // Pointer
  await page.keyboard.up('Shift');
  const focusBeforeBack = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  const box = await page.$eval('textarea', (el) => {
    const r = el.getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
  });
  await page.mouse.click(box.x, box.y);
  await sleep(300);
  const backStatus = await page.$eval('.status', (el) => el.textContent);
  check('from Type mode, a pinch on the text box presses the focused Pointer button',
    focusBeforeBack === 'pointer' && backStatus.trim().endsWith('pointer'), `focus ${focusBeforeBack}, status ${backStatus.replace(/\s+/g, ' ')}`);
  // 15. App shortcuts: buttons 1..N come from the PC's config; pressing one fits that app's
  //     window to the cast area (recorded by the harness) and leaves Pointer focused.
  const appButtons = await page.$$eval('button[data-app]', (els) => els.map((el) => `${el.textContent}:${el.title}`));
  const regionNow = await regionAt();
  const switchesBefore = (await input()).length;
  await tapBar('button[data-app="1"]');
  await sleep(400);
  const switched = (await input()).slice(switchesBefore).filter((a) => a.startsWith('switch '));
  const switchStatus = await page.$eval('.status', (el) => el.textContent);
  const focusAfterSwitch = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  check('app button 1 fits Claude to the cast area and focuses Pointer',
    appButtons.join(',') === '1:Claude,2:Browser' &&
      switched.join('|') === `switch Claude ${regionNow.x},${regionNow.y} ${regionNow.width}x${regionNow.height}` &&
      switchStatus.includes('Claude: switched') && focusAfterSwitch === 'pointer',
    `${appButtons.join(',')} | ${switched.join('|')} | focus ${focusAfterSwitch}`);
  // 16. Back (history.back() on the glasses, or Escape) toggles between the view and the
  //     controls; the same Back arriving both ways counts once.
  await tapBar('button[data-mode="pointer"]'); // Pointer mode, swipes on the view
  await sleep(200);
  const focusName = () => page.evaluate(() => document.activeElement?.dataset?.mode ?? document.activeElement?.tagName);
  // Like the glasses, reset focus to the first button right after the Back navigation.
  await page.evaluate(() => {
    history.back();
    setTimeout(() => document.querySelector('button[data-mode="overview"]')?.focus(), 30);
  });
  await sleep(400);
  const afterHistoryBack = await focusName();
  await page.keyboard.press('Escape');
  await sleep(500);
  const afterEscape = await focusName();
  await page.evaluate(() => history.back());
  await page.keyboard.press('Escape');
  await sleep(500);
  const afterDoubleBack = await focusName();
  const stillInSession = await page.$('.stage').then((el) => el !== null);
  check('Back: view → controls (focus held on Type through a reset) → Pointer, a doubled Back counts once',
    afterHistoryBack === 'type' && afterEscape === 'BODY' && afterDoubleBack === 'type' && stillInSession,
    `history.back ${afterHistoryBack}, Escape ${afterEscape}, both ${afterDoubleBack}`);
  // 17. Pointer-mode swipes are shortcuts by default: down/up scroll, left cycles the apps
  //     (1 → 2 → 1, starting after app 1 from step 15), right opens Type. With Pan on they move the view.
  // A plain click(): focus was left on Type by the keyboard, so a mouse press here would be
  // treated like a pinch and press Type instead (the redirect working as intended).
  const press = (selector) => page.$eval(selector, (el) => el.click());
  await press('button[data-mode="pointer"]');
  await sleep(200);
  const actionsBefore = (await input()).length;
  for (const key of ['ArrowDown', 'ArrowUp', 'ArrowLeft', 'ArrowLeft']) {
    await page.keyboard.press(key);
    await sleep(300);
  }
  // Switches carry the cast area; keep just "switch <app>".
  const swipeActions = (await input()).slice(actionsBefore).map((a) => (a.startsWith('switch ') ? a.split(' ').slice(0, 2).join(' ') : a));
  await page.keyboard.press('ArrowRight');
  await sleep(300);
  const modeAfterRight = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  const pressedApp = await page.$eval('button[data-app][aria-pressed="true"]', (el) => el.dataset.app);
  check('the current app button is highlighted', pressedApp === '1', `app ${pressedApp}`);
  check('Pointer swipes: down/up scroll, left cycles apps, right opens Type',
    swipeActions.join('|') === 'wheel -1080|wheel 1080|switch Browser|switch Claude' && modeAfterRight === 'type',
    `${swipeActions.join('|')} | right → ${modeAfterRight}`);

  // Back from Type (or from the mode bar) goes home to Pointer.
  await page.evaluate(() => history.back());
  await sleep(300);
  const modeAfterBackFromType = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  check('Back from Type returns to Pointer mode', modeAfterBackFromType === 'pointer', modeAfterBackFromType);

  await press('button[data-mode="pointer"]');
  await sleep(100);
  await press('button[data-toggle="pan"]');
  await sleep(200);
  const panOn = await page.$eval('button[data-toggle="pan"]', (el) => el.getAttribute('aria-pressed'));
  const beforePan = await regionAt();
  const panInputBefore = (await input()).length;
  await page.keyboard.press('ArrowRight');
  await sleep(400);
  const afterPan = await regionAt();
  const panInputs = (await input()).slice(panInputBefore).filter((a) => !a.startsWith('move '));
  check('with Pan on, Pointer swipes move the view instead',
    panOn === 'true' && afterPan.x !== beforePan.x && panInputs.length === 0,
    `pan ${panOn}, x ${beforePan.x} -> ${afterPan.x}, other input ${panInputs.join('|')}`);
  // 18. While swipes are on the view the mode bar is hidden and lets taps through: a tap where
  //     the Region button sits clicks in Windows instead of switching mode.
  await tapBar('button[data-mode="pointer"]');
  await sleep(300);
  const hiddenRegion = await page.$eval('.toolbar.top button[data-mode="overview"]', (el) => {
    const r = el.getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2, opacity: getComputedStyle(el.parentElement).opacity };
  });
  const tapsBefore = (await input()).length;
  await page.mouse.click(hiddenRegion.x, hiddenRegion.y);
  await sleep(700);
  const tapClicks = (await input()).slice(tapsBefore).filter((a) => a === 'click Left').length;
  const modeAfterHiddenTap = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  check('the hidden mode bar lets taps through to the desktop',
    hiddenRegion.opacity === '0' && tapClicks === 1 && modeAfterHiddenTap === 'pointer',
    `opacity ${hiddenRegion.opacity}, clicks ${tapClicks}, mode ${modeAfterHiddenTap}`);
} finally {
  await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exitCode = failed ? 1 : 0;

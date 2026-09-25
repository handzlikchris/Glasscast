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
  const lookLabel = await page.$eval('button[title="Display look"]', (el) => el.textContent);
  check('a session starts in Pointer mode with the lifted look',
    statusText.trim().endsWith('pointer') && lookLabel === 'Look: lifted', `${lookLabel}`);
  await shot('2-view');

  // 3. Pointer mode: drag moves, short tap clicks.
  await page.locator('button::-p-text(Pointer)').click();
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
  await page.locator('button::-p-text(Scroll)').click();
  await page.mouse.move(300, 400);
  await page.mouse.down();
  await page.mouse.move(300, 280, { steps: 12 });
  await page.mouse.up();
  await sleep(300);
  actions = await input();
  check('scroll drag sends wheel', actions.some((a) => /^wheel -\d+/.test(a)));

  // 5. Type mode: text and keys are separate; text never presses Enter.
  await page.locator('button::-p-text(Type)').click();
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

  // 6. Overview: drag the region box and commit it.
  await page.locator('button::-p-text(Overview)').click();
  await sleep(500);
  await page.mouse.move(300, 300);
  await page.mouse.down();
  await page.mouse.move(250, 280, { steps: 8 });
  await page.mouse.up();
  await shot('5-overview');
  await page.locator('button::-p-text(Use region)').click();
  await sleep(500);
  const mode = await page.$eval('.status', (el) => el.textContent);
  check('Use region returns to View mode', mode.includes('view'));

  // 7. Edge panning: in Pointer mode, pushing past the right edge slides the region right.
  const before = await (await fetch(`${BASE}/__harness/region`)).json();
  await page.locator('button::-p-text(Pointer)').click();
  await page.mouse.move(60, 300);
  await page.mouse.down();
  await page.mouse.move(590, 300, { steps: 40 });
  await page.mouse.up();
  await sleep(600);
  const after = await (await fetch(`${BASE}/__harness/region`)).json();
  await shot('6-edge-pan');
  check('pushing past the right edge pans the region right', after && before && after.x > before.x && after.y === before.y,
    `x ${before?.x} -> ${after?.x}`);
  await page.locator('button::-p-text(View)').click();
  await sleep(200);

  // 8. Input is rejected in View mode (nothing new recorded).
  const inputsBefore = (await input()).length;
  await page.mouse.click(300, 300);
  await sleep(300);
  check('View mode ignores taps', (await input()).length === inputsBefore);

  // 9. On the controls, a pinch (a tap on the video) presses the focused button. Tab
  //    stands in for the glasses' swipes. Picking Scroll sends swipes back to the view.
  await page.focus('button[data-mode="view"]');
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

  // 10. Swipes move the view by half its size (left here: earlier steps panned right).
  const regionAt = async () => (await fetch(`${BASE}/__harness/region`)).json();
  const beforeSwipe = await regionAt();
  await page.keyboard.press('ArrowLeft');
  await sleep(400);
  const afterSwipe = await regionAt();
  check('a left swipe moves the view left by half its width',
    afterSwipe.x === Math.max(0, beforeSwipe.x - Math.round(beforeSwipe.width / 2)) && afterSwipe.y === beforeSwipe.y,
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
  await page.$eval('textarea', (el) => el.dispatchEvent(new Event('change', { bubbles: true })));
  await sleep(200);
  const afterComposer = await activeName();
  const typedBefore = (await input()).length;
  await pinch(); // presses Send text
  const afterSend = await activeName();
  await pinch(); // presses Enter
  const afterEnter = await activeName();
  const typed = (await input()).slice(typedBefore);
  await pinch(); // presses Pointer
  const roundTripStatus = await page.$eval('.status', (el) => el.textContent);
  check('Type round trip by pinches: text box, Send text, Enter, then Pointer',
    afterType === 'TEXTAREA' && afterComposer === 'Send text' && afterSend === 'Enter' && afterEnter === 'pointer' &&
      typed.join('|') === 'type from the composer|key Enter' && roundTripStatus.trim().endsWith('pointer'),
    `after Type ${afterType}, composer ${afterComposer}, Send ${afterSend}, Enter ${afterEnter}, sent ${typed.join('|')}`);

  // 14. Back out of Type mode: focus the Pointer button with the keyboard, then "pinch" on the
  //     text box (where the glasses' pointer tends to be after typing). Pointer gets pressed.
  await page.locator('button::-p-text(Type)').click();
  await page.locator('textarea').fill('typed on the glasses');
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
} finally {
  await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exitCode = failed ? 1 : 0;

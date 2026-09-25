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
  const pinchThenHold = async () => {
    await page.mouse.click(300, 300);
    await sleep(100);
    await page.mouse.down();
    await sleep(700);
    await page.mouse.up();
    await sleep(200);
  };

  // 11. Pinch, then pinch-hold puts focus on the current mode's button; swipes then stay on the controls.
  await pinchThenHold();
  const heldFocus = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  const beforeControlsSwipe = await regionAt();
  await page.keyboard.press('ArrowRight');
  await sleep(300);
  const afterControlsSwipe = await regionAt();
  check('pinch, then pinch-hold moves focus to the controls', heldFocus === 'scroll' &&
    afterControlsSwipe.x === beforeControlsSwipe.x, `focus ${heldFocus}`);

  // 12. Pointer mode: a pinch clicks after a short wait, two quick pinches double-click,
  //     and pinch-then-hold clicks nothing.
  await page.keyboard.down('Shift');
  await page.keyboard.press('Tab');
  await page.keyboard.up('Shift');
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
  const held = await clicksIn(pinchThenHold);
  const heldToPointer = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  check('in Pointer mode a pinch clicks once and two quick pinches double-click', single === 1 && double === 2,
    `single ${single}, double ${double}`);
  check('in Pointer mode pinch-then-hold opens the controls without clicking', held === 0 && heldToPointer === 'pointer',
    `clicks ${held}, focus ${heldToPointer}`);
} finally {
  await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exitCode = failed ? 1 : 0;

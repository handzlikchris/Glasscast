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
  await sleep(400);
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
} finally {
  await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exitCode = failed ? 1 : 0;

// Drives the real glasses client (client-web/dist) in headless Chrome against the
// e2e harness (tools/e2e-harness), which runs the real server with pairing
// auto-approved and input recorded instead of injected.
//
//   npm run drive -- [--headed] [--screenshots <dir>]
//
// Exit code 0 when every step passed.
import puppeteer from 'puppeteer-core';
import { mkdirSync, readdirSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
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
const startedAt = Date.now();
/** Stats log lines written since this run started (the harness logs to %TEMP%/glasses-e2e-stats). */
const statsLog = () => {
  const dir = join(tmpdir(), 'glasses-e2e-stats');
  let files = [];
  try {
    files = readdirSync(dir).filter((f) => f.endsWith('.jsonl'));
  } catch {}
  return files
    .flatMap((f) => readFileSync(join(dir, f), 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)))
    .filter((l) => Date.parse(l.t) >= startedAt);
};

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

  // 1. The first screen asks PC or Phone, with PC focused (nothing chosen before); a pinch
  //    anywhere presses it. Pairing (auto-approved by the harness) then leads into a session.
  await page.goto(`${BASE}/`, { waitUntil: 'load' });
  await page.waitForSelector('.choose', { timeout: 10_000 });
  const firstFocus = await page.evaluate(() => document.activeElement?.textContent?.trim());
  check('the first screen offers PC or Phone, with PC focused', firstFocus === 'PC', firstFocus);
  await page.mouse.click(300, 560);
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
  // The harness's video pair runs over the LAN (its offer carries the PC's LAN addresses).
  check('the status bar says the video is local', statusText.includes('live (local)'), statusText.replace(/\s+/g, ' '));
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

  // 2b. Stats: the PC's per-frame timings match the frames the video element shows (RTP timestamps).
  await tapBar('button[data-toggle="stats"]');
  const statsShown = await page
    .waitForFunction(() => /e2e \d+ ms/.test(document.querySelector('.stats-panel')?.textContent ?? ''), { timeout: 10_000 })
    .then(() => true, () => false);
  const statsText = await page.$$eval('.stats-panel div', (lines) => lines.map((l) => l.textContent).join(' | '));
  await shot('2b-stats');
  await tapBar('button[data-toggle="stats"]');
  check('the Stats panel shows capture-to-display latency and PC timings',
    statsShown && /PC→here \d+/.test(statsText) && /PC capture \d+/.test(statsText), statsText);
  const logged = statsLog();
  const glassesLine = logged.findLast((l) => l.kind === 'glasses');
  check("the glasses' and the PC's figures reach the PC's stats log",
    typeof glassesLine?.e2eMs === 'number' && glassesLine.framesShown > 0 && logged.some((l) => l.kind === 'pc'),
    glassesLine ? `e2e ${glassesLine.e2eMs}, framesShown ${glassesLine.framesShown}, ${logged.length} lines` : `${logged.length} lines`);

  // 2c. The PC's sound (the harness plays a tone that beeps on and off each second): on by default,
  //     played by an element of its own, its bandwidth in the status bar. ♪ off stops the PC
  //     capturing and sending ("A off"); ♪ on brings it back.
  const audioKbps = async (ms = 6000) => {
    let best = 0;
    for (const until = Date.now() + ms; Date.now() < until && best < 10; ) {
      const text = await page.$eval('[data-bandwidth]', (el) => el.textContent);
      best = Math.max(best, Number(/A (\d+) kbps/.exec(text)?.[1] ?? 0));
      await sleep(250);
    }
    return best;
  };
  // The element only keeps the stream flowing (muted); Web Audio plays it (audioOutput.ts).
  const audioElement = () =>
    page.$eval('audio', (a) => ({
      playing: !!a.srcObject && !a.paused,
      output: document.querySelector('button[data-toggle="audio"]')?.getAttribute('data-output'),
    }));
  const soundOn = await audioKbps();
  const elementOn = await audioElement();
  const pcOn = statsLog().findLast((l) => l.kind === 'pc' && l.audioOn === 1);
  const glassesAudio = statsLog().findLast((l) => l.kind === 'glasses' && l.audioKbps > 0);
  check("the PC's sound plays by default and its bandwidth shows in the status bar",
    soundOn >= 10 && elementOn.playing && elementOn.output === 'running' && pcOn?.audioKbps > 0 && glassesAudio !== undefined,
    `A ${soundOn} kbps, element ${JSON.stringify(elementOn)}, PC ${pcOn?.audioKbps} kbps, glasses logged ${glassesAudio?.audioKbps}`);
  await tapBar('button[data-toggle="audio"]');
  await sleep(2500);
  const offText = await page.$eval('[data-bandwidth]', (el) => el.textContent);
  const offButton = await page.$eval('button[data-toggle="audio"]', (el) => `${el.textContent.trim()} pressed=${el.getAttribute('aria-pressed')}`);
  const pcOff = statsLog().findLast((l) => l.kind === 'pc');
  const elementOff = await audioElement();
  check('♪ off stops the PC capturing and sending, and the status bar says so',
    offText.includes('A off') && offButton === '♪ pressed=false' && pcOff?.audioOn === 0 && pcOff?.audioKbps === 0 && elementOff.playing,
    `${offText}, button ${offButton}, PC on ${pcOff?.audioOn} at ${pcOff?.audioKbps} kbps`);
  await tapBar('button[data-toggle="audio"]');
  const soundAgain = await audioKbps();
  const toggles = statsLog().filter((l) => l.kind === 'event' && l.event === 'setAudio').map((l) => l.on);
  check('♪ on brings the sound back, and each switch is logged',
    soundAgain >= 10 && toggles.join() === 'true,false,true', `A ${soundAgain} kbps, events ${toggles.join()}`);
  await shot('2c-audio');

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

  // 4. (Scrolling is by swipes in Pointer mode now: see step 17.)

  // 5. Type mode: text and keys are separate; text never presses Enter.
  await tapBar('button[data-mode="type"]');
  await page.locator('textarea').fill('hello from e2e\nsecond line');
  await page.locator('button::-p-text(Send text)').click();
  await sleep(200);
  actions = await input();
  check('typed text arrives flattened, without Enter', actions.includes('type hello from e2e second line') && !actions.includes('key Enter'));
  // Dictation can run long: text of any length arrives whole, in 500-character messages.
  const long = Array.from({ length: 130 }, (_, i) => `word${i}`).join(' ');
  const longBefore = actions.length;
  // As the composer does: the whole text at once, then input and change (fill() would type it
  // key by key, far too slowly for this much).
  await page.$eval('textarea', (el, text) => {
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(el, text);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }, long);
  await sleep(200);
  await page.locator('button::-p-text(Send text)').click();
  await sleep(300);
  const pieces = (await input()).slice(longBefore).filter((a) => a.startsWith('type ')).map((a) => a.slice(5));
  check('long text arrives whole, in 500-character messages',
    pieces.length > 1 && pieces.join('') === long && pieces.every((p) => p.length <= 500),
    `${long.length} chars in ${pieces.length} messages (${pieces.map((p) => p.length).join('+')})`);
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

  // 7. Edge panning: in Pointer mode the view stays put when the cursor is pushed past an edge,
  //    unless Pan is on; then pushing past the right edge slides the region right.
  const pushRight = async () => {
    await page.mouse.move(60, 300);
    await page.mouse.down();
    await page.mouse.move(590, 300, { steps: 40 });
    await page.mouse.up();
    await sleep(600);
    return (await fetch(`${BASE}/__harness/region`)).json();
  };
  const before = await (await fetch(`${BASE}/__harness/region`)).json();
  await tapBar('button[data-mode="pointer"]');
  const locked = await pushRight();
  check('without Pan, pushing past the edge leaves the view where it is', locked && before && locked.x === before.x,
    `x ${before?.x} -> ${locked?.x}`);
  // Edge scrolling: without Pan, pushing on past the bottom scrolls down, past the top scrolls up.
  const wheelSince = async (from) => (await input()).slice(from).filter((a) => a.startsWith('wheel ')).map((a) => Number(a.split(' ')[1]));
  // Pointer moves are relative, so each push covers the whole view height to be sure to hit the edge.
  const pushVertical = async (fromY, toY) => {
    const from = (await input()).length;
    await page.mouse.move(300, fromY);
    await page.mouse.down();
    await page.mouse.move(300, toY, { steps: 40 });
    await page.mouse.up();
    await sleep(400);
    return wheelSince(from);
  };
  const downWheel = await pushVertical(10, 590);
  // Up pushes start above the status bar (the bottom 28 px aren't the gesture layer).
  await pushVertical(560, 10); // back up to near the top edge
  const upWheel = await pushVertical(560, 10);
  const scrolledRegion = await (await fetch(`${BASE}/__harness/region`)).json();
  // Recorded as Windows wheel deltas: negative scrolls down, positive up.
  check('without Pan, pushing past the bottom or top edge scrolls down or up',
    downWheel.length > 0 && downWheel.every((w) => w < 0) && upWheel.length > 0 && upWheel.every((w) => w > 0) &&
      scrolledRegion.y === before.y,
    `down ${downWheel.join(',')} | up ${upWheel.join(',')}`);
  await tapBar('button[data-toggle="pan"]'); // Pan on
  const after = await pushRight();
  await tapBar('button[data-toggle="pan"]'); // Pan off again
  await shot('6-edge-pan');
  check('with Pan on, pushing past the right edge pans the region right', after && before && after.x > before.x && after.y === before.y,
    `x ${before?.x} -> ${after?.x}`);

  // 8. Taps don't click in Region mode (nothing new recorded); Cancel returns to Pointer.
  await tapBar('button[data-mode="overview"]');
  await sleep(300);
  const inputsBefore = (await input()).length;
  await page.mouse.click(300, 300);
  await sleep(500);
  check('Region mode ignores taps', (await input()).length === inputsBefore);
  await page.locator('button::-p-text(Cancel)').click();
  await sleep(200);

  // 9. On the controls, a pinch (a tap on the video) presses the focused button. Tab
  //    stands in for the glasses' swipes. Picking Pointer sends swipes back to the view.
  await page.focus('button[data-mode="overview"]');
  let focusedMode = null;
  for (let i = 0; i < 8 && focusedMode !== 'pointer'; i++) {
    await page.keyboard.press('Tab');
    focusedMode = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  }
  await page.mouse.click(300, 300);
  await sleep(300);
  const pinchMode = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  const focusAfterPick = await page.evaluate(() => document.activeElement?.tagName ?? null);
  check('a pinch presses the focused button, then swipes go back to the view',
    pinchMode === 'pointer' && focusAfterPick === 'BODY', `mode ${pinchMode}, focus ${focusAfterPick}`);

  // 10. With Pan on, swipes move the view by a quarter of its size (left here: earlier steps
  //     panned right).
  const regionAt = async () => (await fetch(`${BASE}/__harness/region`)).json();
  await tapBar('button[data-toggle="pan"]'); // Pan on
  await sleep(200);
  const beforeSwipe = await regionAt();
  await page.keyboard.press('ArrowLeft');
  await sleep(400);
  const afterSwipe = await regionAt();
  await tapBar('button[data-toggle="pan"]'); // Pan off again
  await sleep(200);
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

  // 11. Pinch, then pinch-hold puts focus on the likely next mode (Type, from Pointer);
  //     swipes then stay on the controls.
  await pinchThenHold();
  const heldFocus = await page.evaluate(() => document.activeElement?.dataset?.mode ?? null);
  const beforeControlsSwipe = await regionAt();
  await page.keyboard.press('ArrowRight');
  await sleep(300);
  const afterControlsSwipe = await regionAt();
  check('pinch, then pinch-hold from Pointer focuses Type', heldFocus === 'type' &&
    afterControlsSwipe.x === beforeControlsSwipe.x, `focus ${heldFocus}`);

  // 12. Pointer mode: a pinch clicks after a short wait, two quick pinches double-click,
  //     and pinch-then-hold clicks nothing.
  await tapBar('button[data-mode="pointer"]'); // back to Pointer, swipes on the view
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
  // 17. Pointer-mode swipes are shortcuts by default: down/up scroll, a double swipe left (two
  //     within 0.6 s) cycles the apps (1 → 2 → 1, starting after app 1 from step 15), a lone left
  //     swipe does nothing, right opens Type. With Pan on they move the view.
  // A plain click(): focus was left on Type by the keyboard, so a mouse press here would be
  // treated like a pinch and press Type instead (the redirect working as intended).
  const press = (selector) => page.$eval(selector, (el) => el.click());
  await press('button[data-mode="pointer"]');
  await sleep(200);
  const actionsBefore = (await input()).length;
  // [key, wait after]: double left → Browser; a lone left that times out; double left → Claude.
  const swipes = [['ArrowDown', 300], ['ArrowUp', 300], ['ArrowLeft', 150], ['ArrowLeft', 300],
    ['ArrowLeft', 900], ['ArrowLeft', 150], ['ArrowLeft', 300]];
  for (const [key, wait] of swipes) {
    await page.keyboard.press(key);
    await sleep(wait);
  }
  // Switches carry the cast area; keep just "switch <app>".
  const swipeActions = (await input()).slice(actionsBefore).map((a) => (a.startsWith('switch ') ? a.split(' ').slice(0, 2).join(' ') : a));
  await page.keyboard.press('ArrowRight');
  await sleep(300);
  const modeAfterRight = await page.$eval('.status span:last-child', (el) => el.textContent.trim());
  const pressedApp = await page.$eval('button[data-app][aria-pressed="true"]', (el) => el.dataset.app);
  check('the current app button is highlighted', pressedApp === '1', `app ${pressedApp}`);
  check('Pointer swipes: down/up scroll, double left cycles apps (a lone left does not), right opens Type',
    swipeActions.join('|') === 'wheel -360|wheel 360|switch Browser|switch Claude' && modeAfterRight === 'type',
    `${swipeActions.join('|')} | right → ${modeAfterRight}`);

  // 17b. Scroll strength (↕) is per app: one press takes the current app (Claude) from the default
  //      3 notches a swipe to 2. Four more presses wrap it back round to 3 for the steps after this one.
  await press('button[data-mode="pointer"]');
  await sleep(200);
  await tapBar('button[data-scroll]');
  const scrollLabel = await page.$eval('button[data-scroll]', (el) => el.textContent.trim());
  await press('button[data-mode="pointer"]');
  await sleep(200);
  const beforeGentle = (await input()).length;
  await page.keyboard.press('ArrowDown');
  await sleep(300);
  const gentle = (await input()).slice(beforeGentle).filter((a) => a.startsWith('wheel '));
  for (let i = 0; i < 4; i++) await tapBar('button[data-scroll]');
  const restored = await page.$eval('button[data-scroll]', (el) => el.dataset.scroll);
  check('the scroll strength button makes swipes scroll less for this app', scrollLabel === '↕ 2' &&
    gentle.join('|') === 'wheel -240' && restored === '3', `${scrollLabel}, ${gentle.join('|')}, back to ${restored}`);

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
  // 18a. A packet lost mid-stream is NACKed by the browser and sent again by the PC, so the
  //      picture carries on without a keyframe. Figures from the stats log, since the loss.
  const streamSession = statsLog().filter((l) => l.kind === 'event' && l.event === 'start').at(-1)?.session;
  // Loses a packet, waits until done(figures) (10 s at most) plus settleMs, returns the figures.
  const sinceLoss = async (url, done, settleMs = 0) => {
    const lostAt = Date.now();
    const lines = () => statsLog().filter((l) => l.session === streamSession);
    const before = lines().filter((l) => l.kind === 'glasses').at(-1) ?? {};
    await fetch(`${BASE}/__harness/${url}`, { method: 'POST' });
    const figuresNow = () => {
      const now = lines();
      const glasses = now.filter((l) => l.kind === 'glasses').at(-1) ?? {};
      const pc = now.filter((l) => l.kind === 'pc' && Date.parse(l.t) >= lostAt);
      const sum = (key) => pc.reduce((total, l) => total + (l[key] ?? 0), 0);
      return {
        lost: (glasses.lostTotal ?? 0) - (before.lostTotal ?? 0),
        nacks: (glasses.nacks ?? 0) - (before.nacks ?? 0),
        plis: (glasses.plis ?? 0) - (before.plis ?? 0),
        freezes: (glasses.freezes ?? 0) - (before.freezes ?? 0),
        nacked: sum('nacked'),
        resent: sum('resent'),
        asked: sum('keyframeRequests'),
      };
    };
    for (let waited = 0; waited < 10_000 && !done(figuresNow()); waited += 250) {
      await sleep(250);
    }
    await sleep(settleMs);
    return figuresNow();
  };
  // 1.5 s after the resend: long enough for a PLI to show up, had the picture stayed broken.
  const resent = await sinceLoss('lose-packet', (f) => f.resent > 0, 1500);
  check('a packet lost mid-stream is NACKed and resent, with no keyframe needed',
    resent.nacks > 0 && resent.nacked > 0 && resent.resent > 0 && resent.plis === 0,
    `browser nacks ${resent.nacks}, PC nacked ${resent.nacked} resent ${resent.resent}, plis ${resent.plis}, freezes ${resent.freezes}`);

  // 18a'. A packet lost for good (not kept for resending): the browser gives up on it and asks for
  //       a keyframe (PLI), and the PC hears it. By now Chrome adds a bandwidth estimate (REMB) to
  //       its RTCP reports; a PLI inside such a report went unseen before reduced-size RTCP.
  const forGood = await sinceLoss('lose-packet-for-good', (f) => f.plis > 0 && f.asked > 0);
  check('a packet lost for good makes the browser ask for a keyframe and the PC hears it',
    forGood.plis > 0 && forGood.asked > 0, `browser nacks ${forGood.nacks} plis ${forGood.plis}, PC heard ${forGood.asked}`);

  // 18a''. The audio stream started 300 packets before its sequence wrap (harness), so by now it
  //        has crossed it: the sound must still arrive (SRTP rollover, as for the video).
  const firstSession = statsLog().find((l) => l.kind === 'event' && l.event === 'start')?.session;
  const audioSent = statsLog().filter((l) => l.kind === 'pc' && l.session === firstSession).reduce((sum, l) => sum + (l.audioPackets ?? 0), 0);
  const soundLater = await audioKbps();
  check('the sound still arrives after its sequence numbers wrapped', audioSent > 300 && soundLater >= 10,
    `${audioSent} packets sent, A ${soundLater} kbps`);
  // ♪ off is remembered on this device: the next session (the restart below) starts without sound.
  await tapBar('button[data-toggle="audio"]');

  // 18b. Restarting the page reconnects without pairing: the PC remembers approved glasses for a
  //      while (device token). The old session is replaced if the PC hasn't noticed it's gone.
  //      The harness also loses the start of this stream (its first keyframe): the browser asks
  //      for a keyframe (PLI) and the PC answers it; the harness's own keyframes are 30 s apart.
  await fetch(`${BASE}/__harness/lose-stream-start`, { method: 'POST' });
  const reloadedAt = Date.now();
  await page.reload({ waitUntil: 'load' });
  // The first screen again, PC focused (the last choice): one pinch.
  await page.waitForSelector('.choose', { timeout: 10_000 });
  await page.mouse.click(300, 560);
  const resumedLive = await page
    .waitForFunction(() => document.querySelector('.status')?.textContent?.includes('live'), { timeout: 15_000 })
    .then(() => true, () => false);
  await sleep(500);
  const starts = statsLog().filter((l) => l.kind === 'event' && l.event === 'start');
  const pairingSeen = await page.$('.pairing');
  check('restarting the page reconnects without pairing', resumedLive && !pairingSeen && starts.at(-1)?.resumed === true,
    `${starts.length} starts, last resumed ${starts.at(-1)?.resumed}`);
  // Wait for the first picture (frames shown, from the glasses' stats) and the PC's request count.
  let asked = 0;
  let firstPictureMs = null;
  for (let waited = 0; waited < 8000 && (asked === 0 || firstPictureMs === null); waited += 250) {
    await sleep(250);
    const session = statsLog().filter((l) => l.session === starts.at(-1)?.session);
    asked = session.filter((l) => l.kind === 'pc').reduce((sum, l) => sum + (l.keyframeRequests ?? 0), 0);
    const shown = session.find((l) => l.kind === 'glasses' && l.framesShown > 0);
    if (shown && firstPictureMs === null) firstPictureMs = Date.parse(shown.t) - reloadedAt;
  }
  check('a stream whose start was lost asks for a keyframe and the PC answers it at once',
    asked > 0 && firstPictureMs !== null && firstPictureMs < 4000, `asked ${asked}, picture within ${firstPictureMs} ms`);
  await sleep(1500);
  const resumedAudio = statsLog().filter((l) => l.kind === 'pc' && l.session === starts.at(-1)?.session);
  const rememberedButton = await page.$eval('button[data-toggle="audio"]', (el) => `${el.textContent.trim()} pressed=${el.getAttribute('aria-pressed')}`);
  check('♪ off is remembered: the restarted session gets no sound',
    rememberedButton === '♪ pressed=false' && resumedAudio.length > 0 && resumedAudio.every((l) => l.audioOn === 0 && l.audioPackets === 0),
    `button ${rememberedButton}, ${resumedAudio.length} PC lines, packets ${resumedAudio.reduce((s, l) => s + l.audioPackets, 0)}`);
  await tapBar('button[data-toggle="audio"]'); // back on for the rest

  // 18c. The app hidden for 5 s (another glasses app, or closed but kept alive) ends the session on
  //      both sides, so the PC stops showing it as live; back in view, a pinch presses Reconnect.
  const setVisibility = (state) =>
    page.evaluate((s) => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => s });
      document.dispatchEvent(new Event('visibilitychange'));
    }, state);
  await setVisibility('hidden');
  const hiddenAt = Date.now();
  const endedWhileHidden = await page.waitForSelector('main.ended', { timeout: 9000 }).then(() => true, () => false);
  const hiddenMs = Date.now() - hiddenAt;
  await sleep(500);
  const pcSession = await (await fetch(`${BASE}/__harness/session`)).text();
  await setVisibility('visible');
  await page.mouse.click(300, 520);
  const backLive = await page.waitForSelector('.stage', { timeout: 15_000 }).then(() => true, () => false);
  await page
    .waitForFunction(() => document.querySelector('.status')?.textContent?.includes('live'), { timeout: 15_000 })
    .catch(() => {});
  check('a hidden app ends the session on both sides after 5 s, and Reconnect brings it back',
    endedWhileHidden && hiddenMs >= 4500 && (pcSession === '' || pcSession === 'null') && backLive,
    `ended ${endedWhileHidden} after ${hiddenMs} ms, PC session ${pcSession || 'none'}, back ${backLive}`);

  // 19. Ending the session on the PC (tray, Ctrl+Shift+X) keeps the glasses remembered: the ended
  //     screen focuses Reconnect, and a pinch anywhere presses it (the glasses' pinch doesn't land
  //     on the button). The new session is a resume, with no pairing.
  await fetch(`${BASE}/__harness/terminate`, { method: 'POST' });
  const ended = await page.waitForSelector('main.ended', { timeout: 5000 }).then(() => true, () => false);
  const focusedButton = await page.evaluate(() => document.activeElement?.textContent?.trim());
  await page.mouse.click(300, 520);
  const reconnected = await page.waitForSelector('.stage', { timeout: 15_000 }).then(() => true, () => false);
  await sleep(500);
  const lastStart = statsLog().filter((l) => l.kind === 'event' && l.event === 'start').at(-1);
  check('after the PC ends a session, a pinch presses Reconnect and it resumes without pairing',
    ended && focusedButton === 'Reconnect' && reconnected && lastStart?.resumed === true,
    `ended ${ended}, focus ${focusedButton}, new session ${reconnected}, resumed ${lastStart?.resumed}`);

  // 20. The mode bar (with End last) still fits on one row, and End goes back to the PC/Phone
  //     choice rather than the ended screen, with PC (the last choice) focused.
  const bar = await page.$eval('.toolbar.top', (el) => ({ fits: el.scrollWidth <= el.clientWidth, width: el.scrollWidth }));
  await tapBar('button[data-action="end"]');
  const chose = await page.waitForSelector('.choose', { timeout: 5000 }).then(() => true, () => false);
  const chooseFocus = await page.evaluate(() => document.activeElement?.textContent?.trim());
  check('End on the bar goes back to the PC/Phone choice, and the bar fits on one row',
    bar.fits && chose && chooseFocus === 'PC' && !(await page.$('main.ended')),
    `bar ${bar.width}px fits ${bar.fits}, choice ${chose}, focus ${chooseFocus}`);
} finally {
  await browser.close();
}

const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exitCode = failed ? 1 : 0;

// Opens the spike page in the locally installed Chrome (headless), waits for
// enough latency samples and prints window.spikeResults().
//
//   npm run check -- [url] [--seconds 20] [--headed]
//
// Exit code 0 = PASS (p50 latency < 150 ms and fps >= 15), 1 = below target or failed.
import puppeteer from 'puppeteer-core';

const args = process.argv.slice(2);
const url = args.find((a) => a.startsWith('http')) ?? 'http://127.0.0.1:5080/';
const secondsIndex = args.indexOf('--seconds');
const seconds = secondsIndex >= 0 ? Number(args[secondsIndex + 1]) : 20;
const headed = args.includes('--headed');

const chromePath =
  process.env.CHROME_PATH ?? 'C:/Program Files/Google/Chrome/Application/chrome.exe';

const browser = await puppeteer.launch({
  executablePath: chromePath,
  headless: !headed,
  args: ['--autoplay-policy=no-user-gesture-required', '--window-size=640,700'],
});

try {
  const page = await browser.newPage();
  await page.setViewport({ width: 600, height: 600 });
  page.on('console', (msg) => console.log(`[page] ${msg.text()}`));
  page.on('pageerror', (err) => console.log(`[page error] ${err.message}`));

  console.log(`Opening ${url} for ${seconds}s…`);
  await page.goto(url, { waitUntil: 'load' });
  await new Promise((resolve) => setTimeout(resolve, seconds * 1000));

  const results = await page.evaluate(() => window.spikeResults());
  console.log(JSON.stringify(results, null, 2));

  const pass =
    results.samples >= 30 && results.latencyP50 !== null && results.latencyP50 < 150 && results.fps >= 15;
  console.log(pass ? 'PASS' : 'BELOW TARGET / FAILED');
  process.exitCode = pass ? 0 : 1;
} finally {
  await browser.close();
}

// Takes the README screenshots against the running docker compose stack (http://localhost:4200).
// Run by .github/workflows/screenshots.yml; locally:  docker compose up -d --build && node tools/screenshots.mjs
import { chromium, devices } from 'playwright';
import { mkdir } from 'node:fs/promises';

const BASE = process.env.BASE_URL ?? 'http://localhost:4200';
const OUT = process.env.OUT_DIR ?? 'docs/screenshots';
await mkdir(OUT, { recursive: true });

const browser = await chromium.launch();

async function session({ appearance = 'light', viewport = { width: 1440, height: 900 }, device } = {}) {
  const context = await browser.newContext({
    ...(device ?? { viewport, deviceScaleFactor: 1 }),
    colorScheme: appearance,
    locale: 'en-ZA',
    timezoneId: 'Africa/Johannesburg',
  });
  await context.addInitScript((a) => localStorage.setItem('mastene.appearance', a), appearance);
  return context.newPage();
}

async function settle(page) {
  await page.waitForLoadState('networkidle');
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(600);
}

async function signInAs(page, name) {
  await page.goto(`${BASE}/login`);
  await page.getByRole('button', { name: new RegExp(name) }).click();
  await page.waitForURL((url) => !url.pathname.startsWith('/login'));
  await settle(page);
}

async function shot(page, file, fullPage = false) {
  await settle(page);
  await page.screenshot({ path: `${OUT}/${file}`, fullPage });
  console.log('saved', file);
}

// Sign-in page
let page = await session();
await page.goto(`${BASE}/login`);
await page.getByText('Try the demo').waitFor();
await shot(page, 'sign-in.png');

// Landlord
await signInAs(page, 'Thandi Nkosi');
await shot(page, 'landlord-dashboard.png', true);
await page.goto(`${BASE}/issues`);
await shot(page, 'issues.png');
await page.goto(`${BASE}/messages`);
await page.locator('nav[aria-label="Conversations"] a').first().click();
await shot(page, 'messages.png');
await page.goto(`${BASE}/leases`);
await page.locator('table a').first().click();
await shot(page, 'lease.png');
await page.context().close();

page = await session({ appearance: 'dark' });
await signInAs(page, 'Thandi Nkosi');
await shot(page, 'landlord-dashboard-dark.png');
await page.context().close();

// Tenant
page = await session();
await signInAs(page, 'Lerato Mokoena');
await shot(page, 'tenant-home.png', true);
await page.context().close();

page = await session({ device: devices['iPhone 14'] });
await signInAs(page, 'Lerato Mokoena');
await shot(page, 'tenant-home-phone.png');
await page.context().close();

await browser.close();

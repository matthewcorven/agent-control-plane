import { test, expect, waitForInteractive, waitForWebAssembly } from '../fixtures';
import { request as httpRequest } from 'node:http';

test('real Harness delegation, navigation and cancellation via browser in both renderers', async ({ page }) => {
  test.setTimeout(120000);
  const requests: string[] = [];
  page.on('request', r => { if (new URL(r.url()).pathname.startsWith('/api/delegation/')) requests.push(r.url()); });
  async function exercise(renderer: string) {
    await page.goto('/delegation');
    await waitForInteractive(page);
    await expect(page.locator('.delegation-page')).toHaveAttribute('data-renderer', renderer);
    await page.locator('#delegation-prompt').fill('Please solve the task');
    await page.getByRole('button', { name: 'Start', exact: true }).click();
    await expect(page.getByRole('status')).toHaveText('Run: completed');
    await expect(page.locator('.delegation-page')).toContainText('Coordinator received: Actual specialist result');
    await page.getByRole('button', { name: 'Specialist — completed' }).click();
    await expect(page.locator('.delegation-page')).toContainText('Investigate the real task');
    await expect(page.locator('.delegation-page')).toContainText('Actual specialist result');
    await page.getByRole('button', { name: 'Coordinator — completed' }).click();
    await expect(page.locator('.delegation-page')).toContainText('Please solve the task');
    await page.locator('#delegation-prompt').fill('e2e cancellation');
    await page.getByRole('button', { name: 'Start', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Specialist — running' })).toBeVisible();
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.getByRole('status')).toHaveText('Run: cancelled');
    await page.getByRole('button', { name: 'Specialist — cancelled' }).click();
    await expect(page.locator('.delegation-page')).toContainText('e2e cancellation');
    expect(await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }))).not.toMatch(/Bearer |capability|[A-F0-9]{64}/);
  }
  await exercise('Server');
  await waitForWebAssembly(page);
  await exercise('WebAssembly');
  expect(requests.length).toBeGreaterThan(0);
  expect(requests.every(url => new URL(url).origin === new URL(page.url()).origin && !new URL(url).search)).toBe(true);
});

test('proxy rejects wrong origin, missing provenance, queries and oversized bodies', async ({ request }) => {
  const url = '/api/delegation/start';
  const headers = { Origin: 'http://127.0.0.1:5499', 'Sec-Fetch-Site': 'same-origin', 'X-Delegation-Request': '1' };
  expect((await request.post(url, { headers: { ...headers, Origin: 'http://127.0.0.1:5498' }, data: { prompt: 'test' } })).status()).toBe(403);
  expect((await request.post(url, { headers: { ...headers, 'Sec-Fetch-Site': 'cross-site' }, data: { prompt: 'test' } })).status()).toBe(403);
  expect((await request.post(url + '?token=leak', { headers, data: { prompt: 'test' } })).status()).toBe(403);
  expect((await request.post(url, { headers, data: { prompt: 'x'.repeat(17000) } })).status()).toBe(413);
  const chunkedStatus = await new Promise<number>((resolve, reject) => {
    const req = httpRequest('http://127.0.0.1:5499/api/delegation/start', { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json', 'Transfer-Encoding': 'chunked' } }, res => { res.resume(); resolve(res.statusCode!); });
    req.on('error', reject);
    req.write('{"prompt":"'); req.write('x'.repeat(17000)); req.end('"}');
  });
  expect(chunkedStatus).toBe(413);
  expect((await request.post('http://127.0.0.1:5399/api/delegation/start', { headers: { ...headers, Origin: 'http://127.0.0.1:5498' }, data: { prompt: 'test' } })).status()).toBe(403);
});

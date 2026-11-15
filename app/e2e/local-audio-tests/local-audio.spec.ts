import { test, expect, waitForInteractive } from '../fixtures';

test('transcript runs through AG-UI before explicitly requested speech', async ({ page, request }) => {
  await page.goto('/local-audio');
  await waitForInteractive(page);
  const wav = Buffer.alloc(44);
  wav.write('RIFF', 0); wav.write('WAVE', 8);
  await page.locator('#audio-file').setInputFiles({ name: 'sample.wav', mimeType: 'audio/wav', buffer: wav });
  await page.getByRole('button', { name: 'Transcribe' }).click();
  await expect(page.locator('#agent-prompt')).toHaveValue('audio pipeline canary');
  await page.getByRole('button', { name: 'Ask agent' }).click();
  await expect(page.locator('#agent-response')).toHaveText('E2E echo: audio pipeline canary');
  await expect(page.locator('#speech-text')).toHaveValue('');
  await expect(page.locator('audio')).toHaveCount(0);
  expect(await (await request.get('http://127.0.0.1:5399/state')).json()).toEqual([]);
  await page.getByRole('button', { name: 'Use response for speech' }).click();
  await expect(page.locator('#speech-text')).toHaveValue('E2E echo: audio pipeline canary');
  expect(await (await request.get('http://127.0.0.1:5399/state')).json()).toEqual([]);
  await page.getByRole('button', { name: 'Generate WAV' }).click();
  await expect(page.locator('audio')).toHaveCount(1);
  expect(await (await request.get('http://127.0.0.1:5399/state')).json()).toMatchObject([
    { input: 'E2E echo: audio pipeline canary', response_format: 'wav' },
  ]);
});

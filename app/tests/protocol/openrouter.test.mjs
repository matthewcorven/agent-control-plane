import { strict as assert } from 'node:assert';
import { sendOpenRouter } from '../../src/Client.Wasm/wwwroot/openrouter-workflow.mjs';

let calls = 0;
const fake = async (url, options) => {
    calls++;
    assert.equal(url, 'https://openrouter.ai/api/v1/chat/completions');
    assert.equal(options.redirect, 'error');
    assert.equal(options.credentials, 'omit');
    assert.deepEqual(JSON.parse(options.body).messages, [{ role: 'user', content: 'hello' }]);
    return new Response(JSON.stringify({ choices: [{ message: { content: 'actual reply' } }] }));
};
assert.equal(await sendOpenRouter('test-key', 'test/model', 'hello', fake), 'actual reply');
await assert.rejects(sendOpenRouter('test-key', 'bad\nmodel', 'hello', fake));
assert.equal(calls, 1);
await assert.rejects(sendOpenRouter('test-key', 'test/model', 'hello', async () => new Response('x', { status: 401 })));
await assert.rejects(sendOpenRouter('test-key', 'test/model', 'hello', async () => new Response('x'.repeat(1048577))));
console.log('OpenRouter workflow tests passed');

const fields = {
    'openrouter-key': { value: 'transient-key', disabled: true },
    'openrouter-model': { value: 'test/model', disabled: true },
    'openrouter-prompt': { value: 'hello', disabled: true },
    'openrouter-result': { textContent: '' },
    'openrouter-send': { disabled: true }
};
let submit;
const form = { dataset: {}, elements: [fields['openrouter-key'], fields['openrouter-model'], fields['openrouter-prompt'], fields['openrouter-send']], addEventListener: (_, fn) => { submit = fn; } };
globalThis.document = { getElementById: id => id === 'openrouter-form' ? form : fields[id] };
const previousFetch = globalThis.fetch;
globalThis.fetch = async (_, options) => {
    assert.equal(fields['openrouter-key'].value, '');
    assert.equal(options.headers.Authorization, 'Bearer transient-key');
    throw new Error('sensitive provider error');
};
const { initialize } = await import('../../src/Client.Wasm/wwwroot/openrouter-workflow.mjs');
initialize();
let prevented = false;
await submit({ preventDefault: () => { prevented = true; } });
assert.equal(prevented, true);
assert.equal(fields['openrouter-key'].value, '');
assert.ok(!fields['openrouter-result'].textContent.includes('sensitive'));
assert.equal(fields['openrouter-send'].disabled, false);
globalThis.fetch = previousFetch;

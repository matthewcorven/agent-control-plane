import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

test('browser requires approval for read and snapshots the reviewed download', async () => {
    const source = await readFile(new URL('../../src/Client.Wasm/wwwroot/file-workflow.mjs', import.meta.url));
    const server = createServer((req, res) => {
        if (req.url === '/module.mjs') { res.writeHead(200, {'content-type':'text/javascript'}); res.end(source); }
        else { res.writeHead(200, {'content-type':'text/html'}); res.end('<div id="root"></div><script type="module">import {mount} from "/module.mjs"; mount(document.querySelector("#root"));</script>'); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await chromium.launch({ headless: true });
        const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}`);
        await page.getByLabel('Select a text file').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('hello') });
        await page.getByText('Request read').click();
        assert.equal(await page.getByLabel('Text content').inputValue(), '');
        await page.getByText('Deny operation').click();
        assert.equal(await page.getByLabel('Text content').inputValue(), '');
        await page.getByText('Request read').click();
        await page.getByText('Approve exact operation').click();
        await page.getByRole('status').getByText('Read notes.txt.').waitFor();
        assert.equal(await page.getByLabel('Text content').inputValue(), 'hello');
        await page.getByLabel('Text content').fill('reviewed');
        await page.getByText('Request download').click();
        await page.getByLabel('Text content').fill('changed after review');
        await page.evaluate(() => { window.saved = null; HTMLAnchorElement.prototype.click = function() { window.saved = { name: this.download, url: this.href }; }; });
        await page.getByText('Approve exact operation').click();
        const saved = await page.evaluate(async () => ({ name: window.saved.name, text: await (await fetch(window.saved.url)).text() }));
        assert.deepEqual(saved, { name: 'edited.txt', text: 'reviewed' });
    } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
});

test('agent sends only the reviewed snapshot after approval and downloads reviewed output', async () => {
    const source = await readFile(new URL('../../src/Client.Wasm/wwwroot/file-workflow.mjs', import.meta.url));
    const requests = [];
    const server = createServer(async (req, res) => {
        if (req.url === '/module.mjs') { res.writeHead(200, {'content-type':'text/javascript'}); res.end(source); }
        else if (req.url === '/api/file-agent/run') {
            let body = ''; for await (const chunk of req) body += chunk;
            requests.push(JSON.parse(body)); res.writeHead(200, {'content-type':'application/json'}); res.end(JSON.stringify({output:'agent suggestion'}));
        } else { res.writeHead(200, {'content-type':'text/html'}); res.end('<div id="root"></div><script type="module">import {mount} from "/module.mjs"; mount(document.querySelector("#root"));</script>'); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await chromium.launch({ headless: true }); const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}`);
        await page.getByLabel('Select a text file').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('selected text') });
        await page.getByText('Request read').click(); await page.getByText('Approve exact operation').click();
        await page.getByLabel('Agent instruction').fill('rewrite this');
        await page.getByText('Review agent request').click();
        assert.match(await page.getByLabel('Agent request approval').innerText(), /selected text/);
        await page.getByText('Deny agent request').click(); assert.equal(requests.length, 0);
        await page.getByText('Review agent request').click();
        await page.getByLabel('Agent instruction').fill('changed after review');
        await page.getByText('Approve sending exact content to local model').click();
        await page.getByRole('status').getByText(/Agent suggestion received/).waitFor();
        assert.deepEqual(requests, [{content:'selected text', instruction:'rewrite this'}]);
        assert.equal(await page.getByLabel('Agent suggested output').inputValue(), 'agent suggestion');
        await page.getByText('Append suggestion to editor').click();
        assert.equal(await page.getByLabel('Text content').inputValue(), 'selected textagent suggestion');
        await page.getByText('Request download').click();
        assert.match(await page.getByLabel('Operation approval').innerText(), /selected textagent suggestion/);
    } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
});

test('a late result cannot populate a different selection', async () => {
    const source = await readFile(new URL('../../src/Client.Wasm/wwwroot/file-workflow.mjs', import.meta.url));
    const server = createServer((req, res) => {
        if (req.url === '/module.mjs') { res.writeHead(200, {'content-type':'text/javascript'}); res.end(source); }
        else { res.writeHead(200, {'content-type':'text/html'}); res.end('<div id="root"></div><script type="module">import {mount} from "/module.mjs"; mount(document.querySelector("#root"));</script>'); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    let browser;
    try {
        browser = await chromium.launch({ headless: true }); const page = await browser.newPage();
        await page.goto(`http://127.0.0.1:${server.address().port}`);
        await page.evaluate(() => { window.fetch = () => new Promise(resolve => { window.finishRequest = () => resolve(new Response(JSON.stringify({ output: 'stale output' }), {status: 200})); }); });
        await page.getByLabel('Select a text file').setInputFiles({name:'first.txt', mimeType:'text/plain', buffer:Buffer.from('first')});
        await page.getByText('Request read').click(); await page.getByText('Approve exact operation').click();
        await page.getByLabel('Agent instruction').fill('summarize'); await page.getByText('Review agent request').click();
        await page.getByText('Approve sending exact content to local model').click();
        await page.getByLabel('Select a text file').setInputFiles({name:'second.txt', mimeType:'text/plain', buffer:Buffer.from('second')});
        await page.evaluate(() => window.finishRequest());
        await page.getByText('Review agent request').waitFor({state:'visible'});
        await page.waitForFunction(() => !Array.from(document.querySelectorAll('button')).find(x => x.textContent === 'Review agent request').disabled);
        assert.equal(await page.getByLabel('Agent suggested output').inputValue(), '');
    } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
});

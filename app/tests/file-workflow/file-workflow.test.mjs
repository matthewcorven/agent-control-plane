import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FileWorkflow, MAX_BYTES } from '../../src/Client.Wasm/wwwroot/file-workflow.mjs';

function file(text, name = 'notes.txt') {
    const bytes = new TextEncoder().encode(text);
    let reads = 0;
    return { name, size: bytes.length, arrayBuffer: async () => { reads++; return bytes.buffer; }, get reads() { return reads; } };
}
test('selection and denial never read; approval is single-use', async () => {
    const workflow = new FileWorkflow(), selected = file('hello');
    assert.deepEqual(workflow.proposeRead(selected), {kind: 'read', name: 'notes.txt', bytes: 5});
    assert.equal(selected.reads, 0);
    workflow.deny();
    await assert.rejects(workflow.approve());
    assert.equal(selected.reads, 0);
    workflow.proposeRead(selected);
    assert.equal((await workflow.approve()).content, 'hello');
    assert.equal(selected.reads, 1);
    await assert.rejects(workflow.approve());
});
test('download approval binds the exact filename and content and is single-use', async () => {
    const workflow = new FileWorkflow();
    assert.deepEqual(workflow.proposeDownload('out.txt', 'new content'), {kind: 'download', name: 'out.txt', bytes: 11, content: 'new content'});
    assert.deepEqual(await workflow.approve(), {kind: 'download', name: 'out.txt', content: 'new content'});
    await assert.rejects(workflow.approve());
    workflow.proposeDownload('a.txt', 'a'); workflow.deny();
    await assert.rejects(workflow.approve());
});
test('invalid proposals clear previous approvals and enforce byte bounds and UTF-8', async () => {
    const workflow = new FileWorkflow();
    workflow.proposeDownload('a.txt', 'a');
    assert.throws(() => workflow.proposeDownload('../outside', 'a'));
    await assert.rejects(workflow.approve());
    assert.throws(() => workflow.proposeDownload('a.txt', 'é'.repeat(MAX_BYTES / 2 + 1)));
    assert.throws(() => workflow.proposeRead({ name:'big', size: MAX_BYTES + 1 }));
    workflow.proposeRead({ name:'bad', size:1, arrayBuffer: async () => new Uint8Array([255]).buffer });
    await assert.rejects(workflow.approve());
    workflow.proposeRead({ name:'lying', size:1, arrayBuffer: async () => new ArrayBuffer(MAX_BYTES + 1) });
    await assert.rejects(workflow.approve());
});

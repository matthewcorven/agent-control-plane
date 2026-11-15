// Capabilities stay in this tab's module memory; they are never returned to .NET or persisted.
const runs = new Map();
let nextId = 1;
const maxResponse = 512 * 1024;
window.addEventListener('pagehide', () => {
    for (const run of runs.values()) {
        run.released = true;
        if (run.token) fetch('/api/delegation/cancel', {
            method: 'POST', keepalive: true, credentials: 'omit', cache: 'no-store',
            headers: { 'X-Delegation-Request': '1', Authorization: `Bearer ${run.token}` }
        }).catch(() => {});
    }
    runs.clear();
});

async function request(operation, token, body) {
    const headers = { 'X-Delegation-Request': '1' };
    if (token) headers.Authorization = `Bearer ${token}`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(`/api/delegation/${operation}`, {
        method: 'POST', headers, body: body === undefined ? undefined : JSON.stringify(body),
        credentials: 'omit', cache: 'no-store', redirect: 'error'
    });
    if (!response.ok) throw new Error(`Delegation request failed (${response.status}).`);
    const reader = response.body.getReader();
    const chunks = [];
    let length = 0;
    try {
        while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            length += value.length;
            if (length > maxResponse) throw new Error('Delegation response exceeds its limit.');
            chunks.push(value);
        }
    } finally { await reader.cancel(); }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    if (!length) return null;
    try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
    catch { throw new Error('Invalid delegation response.'); }
}

export function create() {
    const id = nextId++;
    runs.set(id, { token: null, snapshot: "", released: false });
    return id;
}
export async function start(id, prompt) {
    const run = runs.get(id);
    if (!run) throw new Error("Run is unavailable.");
    if (typeof prompt !== 'string' || prompt.length < 1 || prompt.length > 4000) throw new Error('Invalid prompt.');
    const response = await request('start', null, { prompt });
    if (!response || !/^[0-9A-F]{64}$/.test(response.capability)) throw new Error('Invalid delegation response.');
    if (run.released) { await request("cancel", response.capability); return; }
    run.token = response.capability;
}
export async function refresh(id) {
    const run = runs.get(id);
    if (!run) throw new Error('Run is unavailable.');
    run.snapshot = JSON.stringify(await request('status', run.token));
    return run.snapshot.length;
}
export function read(id, offset) {
    const run = runs.get(id);
    if (!run || !Number.isSafeInteger(offset) || offset < 0 || offset > run.snapshot.length) throw new Error('Invalid snapshot read.');
    let end = Math.min(offset + 4096, run.snapshot.length);
    if (end < run.snapshot.length && /[\uD800-\uDBFF]/.test(run.snapshot[end - 1])) end--;
    return run.snapshot.slice(offset, end);
}
export async function cancel(id) {
    const run = runs.get(id);
    if (run) await request('cancel', run.token);
}
export async function release(id, cancelRunning) {
    const run = runs.get(id);
    if (!run) return;
    runs.delete(id);
    run.released = true;
    if (cancelRunning && run.token) await request('cancel', run.token);
}

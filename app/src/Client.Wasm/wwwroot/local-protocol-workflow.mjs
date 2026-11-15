export async function run(backend, mode, message, left, right) {
    const base = new URL(backend);
    if (!['http:', 'https:'].includes(base.protocol) || !['localhost', '127.0.0.1', '[::1]'].includes(base.hostname) || base.username || base.password) throw new Error('Invalid local server.');
    const response = await fetch(new URL('/api/local-protocols', base), {
        method: 'POST', redirect: 'error', credentials: 'omit',
        headers: { 'Content-Type': 'application/json', 'X-Protocol-Workflow': '1' },
        body: JSON.stringify({ mode, message, left, right }), signal: AbortSignal.timeout(25000)
    });
    if (!response.ok) throw new Error('Local request failed.');
    const data = await response.json();
    if (!Array.isArray(data.events) || data.events.length > 51 || data.events.some(x => typeof x !== 'string' || x.length > 2000)) throw new Error('Invalid response.');
    return data.events;
}

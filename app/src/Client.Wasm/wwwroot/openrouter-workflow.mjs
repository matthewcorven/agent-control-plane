const endpoint = 'https://openrouter.ai/api/v1/chat/completions';
const maxBytes = 1048576;

export async function sendOpenRouter(key, model, prompt, fetchImpl = fetch) {
    if (!key || key.length > 1024 || /[\r\n]/.test(key) || !model || model.length > 200 || /[\x00-\x1f\x7f]/.test(model) || !prompt.trim() || prompt.length > 4000)
        throw new Error('Invalid input.');
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 90000);
    try {
        const response = await fetchImpl(endpoint, {
            method: 'POST', redirect: 'error', credentials: 'omit', cache: 'no-store',
            headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${key}` },
            body: JSON.stringify({ model, messages: [{ role: 'user', content: prompt }], max_tokens: 512 }),
            signal: controller.signal
        });
        if (!response.ok || !response.body) throw new Error('Request failed.');
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let result = '', bytes = 0;
        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            bytes += value.byteLength;
            if (bytes > maxBytes) { await reader.cancel(); throw new Error('Response too large.'); }
            result += decoder.decode(value, { stream: true });
        }
        result += decoder.decode();
        const content = JSON.parse(result)?.choices?.[0]?.message?.content;
        if (typeof content !== 'string' || content.length > 100000) throw new Error('Invalid response.');
        return content;
    } finally { clearTimeout(timer); }
}

export function initialize() {
    const form = document.getElementById('openrouter-form');
    if (!form || form.dataset.bound) return;
    form.addEventListener('submit', async event => {
        event.preventDefault();
        const key = document.getElementById('openrouter-key');
        const model = document.getElementById('openrouter-model');
        const prompt = document.getElementById('openrouter-prompt');
        const result = document.getElementById('openrouter-result');
        const button = document.getElementById('openrouter-send');
        const secret = key.value;
        key.value = '';
        result.textContent = 'Sending to OpenRouter…';
        button.disabled = true;
        try { result.textContent = await sendOpenRouter(secret, model.value.trim(), prompt.value); }
        catch { result.textContent = 'Request failed. Check the model, key, account, network and browser CORS settings.'; }
        finally { button.disabled = false; }
    });
    form.dataset.bound = 'true';
    for (const element of form.elements) element.disabled = false;
}

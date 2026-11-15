// Selected content is sent to the local model only after explicit approval.
export const MAX_BYTES = 128 * 1024;
const encoder = new TextEncoder();

export class FileWorkflow {
    #pending = null;
    #read = null;
    proposeRead(file) {
        this.#pending = null;
        if (!file || file.size > MAX_BYTES) throw new Error('Select a file no larger than 128 KiB.');
        this.#pending = { kind: 'read', file };
        return { kind: 'read', name: file.name, bytes: file.size };
    }
    proposeDownload(name, content) {
        this.#pending = null;
        if (typeof name !== 'string' || !name.trim() || name.length > 120 || /[\\/\x00-\x1f\x7f]/.test(name) || name === '.' || name === '..') throw new Error('Enter a plain download filename.');
        if (typeof content !== 'string' || encoder.encode(content).length > MAX_BYTES) throw new Error('Output exceeds 128 KiB.');
        this.#pending = { kind: 'download', name, content };
        return { kind: 'download', name, bytes: encoder.encode(content).length, content };
    }
    deny() { this.#pending = null; }
    async approve() {
        const op = this.#pending;
        this.#pending = null;
        if (!op) throw new Error('No operation awaiting approval.');
        if (op.kind === 'download') return op;
        if (this.#read) throw new Error('A file read is already in progress.');
        this.#read = op;
        try {
            const bytes = await op.file.arrayBuffer();
            if (bytes.byteLength > MAX_BYTES) throw new Error('File exceeds 128 KiB.');
            return { kind: 'read', name: op.file.name, content: new TextDecoder('utf-8', { fatal: true }).decode(bytes) };
        } finally { this.#read = null; }
    }
}

export function mount(root) {
    const workflow = new FileWorkflow();
    let alive = true, selectedContent = null, selectedName = null, pendingAgent = null, controller = null, selectionVersion = 0;
    const el = (tag, text, parent = root) => { const node = document.createElement(tag); if (text) node.textContent = text; parent.append(node); return node; };
    el('p', 'Select and approve a UTF-8 file. An optional Harness agent can read the selected content after separate approval and send it to local oMLX at 127.0.0.1:8001, model Qwen3.8-27B-oQ4e-mtp. Do not use secrets. No server filesystem access or automatic file writes.');
    const input = el('input'); input.type = 'file'; input.accept = '.txt,.md,.csv,.json,text/plain'; input.setAttribute('aria-label', 'Select a text file');
    const read = el('button', 'Request read');
    const label = el('label', 'Text content');
    const editor = el('textarea', '', label); editor.rows = 12; editor.cols = 70;
    const nameLabel = el('label', 'Download filename');
    const name = el('input', '', nameLabel); name.value = 'edited.txt'; name.maxLength = 120;
    const save = el('button', 'Request download');
    const taskLabel = el('label', 'Agent instruction');
    const task = el('textarea', '', taskLabel); task.maxLength = 4000;
    const agentRequest = el('button', 'Review agent request');
    const agentReview = el('section'); agentReview.setAttribute('aria-label', 'Agent request approval'); agentReview.hidden = true;
    const agentDetails = el('pre', '', agentReview); agentDetails.style.whiteSpace = 'pre-wrap';
    const agentApprove = el('button', 'Approve sending exact content to local model', agentReview);
    const agentDeny = el('button', 'Deny agent request', agentReview);
    const outputLabel = el('label', 'Agent suggested output');
    const output = el('textarea', '', outputLabel); output.rows = 8; output.cols = 70;
    const append = el('button', 'Append suggestion to editor');
    const replace = el('button', 'Replace editor with suggestion');
    agentRequest.addEventListener('click', () => {
        pendingAgent = null; agentReview.hidden = true;
        if (selectedContent === null || !task.value.trim() || task.value.length > 4000) { status.textContent = 'Read a selected file and enter an instruction first.'; return; }
        pendingAgent = { content: selectedContent, instruction: task.value, name: selectedName };
        agentDetails.textContent = `Send to local oMLX 127.0.0.1:8001, model Qwen3.8-27B-oQ4e-mtp\nSelected file: ${pendingAgent.name}\nInstruction: ${pendingAgent.instruction}\n\nExact content:\n${pendingAgent.content}`;
        agentReview.hidden = false;
    });
    agentDeny.addEventListener('click', () => { pendingAgent = null; agentReview.hidden = true; agentDetails.textContent = ''; status.textContent = 'Agent request denied.'; });
    agentApprove.addEventListener('click', async () => {
        const approved = pendingAgent; pendingAgent = null; agentReview.hidden = true; agentDetails.textContent = '';
        if (!approved || controller) return;
        const version = selectionVersion;
        controller = new AbortController(); agentApprove.disabled = true; agentRequest.disabled = true;
        try {
            const response = await fetch('/api/file-agent/run', { method: 'POST', credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal,
                headers: { 'Content-Type': 'application/json', 'X-File-Agent': '1' }, body: JSON.stringify({ content: approved.content, instruction: approved.instruction }) });
            if (!response.ok) throw new Error('Agent request failed.');
            const reader = response.body.getReader(); const chunks = []; let length = 0;
            try { while (true) { const { done, value } = await reader.read(); if (done) break; length += value.length; if (length > 1024 * 1024) throw new Error('Response too large.'); chunks.push(value); } }
            finally { await reader.cancel(); }
            const bytes = new Uint8Array(length); let offset = 0; for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
            const result = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
            if (typeof result.output !== 'string' || encoder.encode(result.output).length > MAX_BYTES) throw new Error('Invalid result.');
            if (alive && version === selectionVersion) { output.value = result.output; status.textContent = 'Agent suggestion received; review it before downloading.'; }
        } catch { if (alive && version === selectionVersion) status.textContent = 'Agent request failed or was cancelled; no file was changed.'; }
        finally { controller = null; agentApprove.disabled = false; agentRequest.disabled = false; }
    });
    append.addEventListener('click', () => { if (encoder.encode(editor.value + output.value).length <= MAX_BYTES) editor.value += output.value; else status.textContent = 'Combined content exceeds 128 KiB.'; });
    replace.addEventListener('click', () => { if (encoder.encode(output.value).length <= MAX_BYTES) editor.value = output.value; else status.textContent = 'Suggestion exceeds 128 KiB.'; });
    const review = el('section'); review.setAttribute('aria-label', 'Operation approval');
    const details = el('pre', '', review); details.style.whiteSpace = 'pre-wrap';
    const yes = el('button', 'Approve exact operation', review);
    const no = el('button', 'Deny operation', review);
    const status = el('p'); status.setAttribute('role', 'status');
    review.hidden = true;
    function reset() { workflow.deny(); review.hidden = true; details.textContent = ''; }
    function propose(fn) {
        reset(); status.textContent = '';
        try {
            const op = fn();
            details.textContent = op.kind === 'read' ? `Read selected file: ${op.name}\nSize: ${op.bytes} bytes` : `Download: ${op.name}\nSize: ${op.bytes} bytes\n\nExact content:\n${op.content}`;
            review.hidden = false;
        } catch (error) { status.textContent = error.message; }
    }
    input.addEventListener('change', () => { selectionVersion++; reset(); controller?.abort(); selectedContent = null; selectedName = null; pendingAgent = null; output.value = ''; agentReview.hidden = true; agentDetails.textContent = '';  });
    read.addEventListener('click', () => propose(() => workflow.proposeRead(input.files?.[0])));
    save.addEventListener('click', () => propose(() => workflow.proposeDownload(name.value, editor.value)));
    no.addEventListener('click', () => { reset(); status.textContent = 'Operation denied.'; });
    yes.addEventListener('click', async () => {
        yes.disabled = true; no.disabled = true; input.disabled = true; read.disabled = true; save.disabled = true;
        try {
            const result = await workflow.approve();
            if (!alive) return;
            review.hidden = true; details.textContent = '';
            if (result.kind === 'read') { selectionVersion++; controller?.abort(); output.value = ''; selectedContent = result.content; selectedName = result.name; pendingAgent = null; agentReview.hidden = true; agentDetails.textContent = ''; editor.value = result.content; status.textContent = `Read ${result.name}.`; }
            else {
                const url = URL.createObjectURL(new Blob([result.content], { type: 'text/plain;charset=utf-8' }));
                try { const link = document.createElement('a'); link.href = url; link.download = result.name; link.click(); }
                finally { setTimeout(() => URL.revokeObjectURL(url), 1000); }
                status.textContent = 'Download requested; your browser decides where to save it.';
            }
        } catch { if (alive) { reset(); status.textContent = 'Operation failed: use a UTF-8 text file within the size limit.'; } }
        finally { yes.disabled = false; no.disabled = false; input.disabled = false; read.disabled = false; save.disabled = false; }
    });
    return { dispose() { alive = false; controller?.abort(); pendingAgent = null; selectedContent = null; reset(); root.replaceChildren(); } };
}

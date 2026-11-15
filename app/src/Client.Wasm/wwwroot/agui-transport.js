const requests = new Map();
const checkpoints = new Map();
let nextId = 0;
const chunkSize = 8192;
const maxRequestBytes = 256 * 1024;
const maxCheckpointBytes = 256 * 1024;
const lifetime = 30 * 60 * 1000;

export function create(path) {
    if (requests.size >= 4) throw new Error("Too many active agent requests.");
    if (!/^\/ag-ui(?:\/(?:tools|approval|shared-state|predictive-state|generative-ui|reasoning|harness-planning|harness-loop|harness-memory|harness-skills|harness-assistant))?$/.test(path))
        throw new Error("Unsupported agent route.");
    const id = ++nextId;
    requests.set(id, { path, controller: new AbortController(), parts: [], size: 0, pending: null });
    return id;
}

export function append(id, bytes) {
    const request = requests.get(id);
    if (!request || bytes.length > chunkSize || request.size + bytes.length > maxRequestBytes)
        throw new Error("Agent request exceeds the browser transport limit.");
    request.parts.push(bytes);
    request.size += bytes.length;
}

export async function start(id) {
    const request = requests.get(id);
    const body = new Uint8Array(request.size);
    let offset = 0;
    for (const part of request.parts) {
        body.set(part, offset);
        offset += part.length;
    }
    const response = await fetch(request.path, {
        method: "POST", credentials: "same-origin", redirect: "error", cache: "no-store",
        headers: { "Content-Type": "application/json", "Accept": "text/event-stream" },
        body, signal: request.controller.signal
    });
    request.parts = [];
    request.reader = response.body.getReader();
    return { status: response.status, contentType: response.headers.get("Content-Type") };
}

export async function read(id) {
    const request = requests.get(id);
    if (!request) throw new Error("Agent request is no longer available.");
    if (!request.pending) {
        const { value, done } = await request.reader.read();
        if (done) return new Uint8Array();
        if (value.length > maxRequestBytes) {
            abort(id);
            throw new Error("Agent stream chunk exceeds the browser transport limit.");
        }
        request.pending = value;
    }
    const value = request.pending.slice(0, chunkSize);
    request.pending = request.pending.length > chunkSize ? request.pending.subarray(chunkSize) : null;
    return value;
}

export function abort(id) {
    const request = requests.get(id);
    if (!request) return;
    request.controller.abort();
    requests.delete(id);
}

function key(route) { return `agui-scenario:${route}`; }
function load(route) {
    const text = sessionStorage.getItem(key(route));
    if (!text) return null;
    let item;
    try { item = JSON.parse(text); }
    catch { sessionStorage.removeItem(key(route)); throw new Error("Conversation checkpoint is invalid. Start a new conversation."); }
    if (new TextEncoder().encode(text).length > maxCheckpointBytes ||
        !Number.isFinite(item.savedAt) || item.savedAt > Date.now() ||
        Date.now() - item.savedAt >= lifetime) {
        sessionStorage.removeItem(key(route));
        return null;
    }
    if (typeof item.content !== "string") {
        sessionStorage.removeItem(key(route));
        throw new Error("Conversation checkpoint is invalid. Start a new conversation.");
    }
    return item.content;
}
function save(route, content) {
    const text = JSON.stringify({ savedAt: Date.now(), content });
    if (new TextEncoder().encode(text).length > maxCheckpointBytes) {
        sessionStorage.removeItem(key(route));
        throw new Error("Conversation exceeds the tab checkpoint limit. Start a new conversation.");
    }
    sessionStorage.setItem(key(route), text);
}
export function clear(route) { sessionStorage.removeItem(key(route)); }

export function beginCheckpoint(route, reading) {
    if (checkpoints.size >= 4) throw new Error("Too many checkpoint operations.");
    const content = reading ? load(route) : null;
    if (reading && content === null) return { id: 0, length: 0 };
    const bytes = reading ? new TextEncoder().encode(content) : null;
    const id = ++nextId;
    checkpoints.set(id, { route, bytes, parts: [], offset: 0, size: 0 });
    return { id, length: bytes?.length ?? 0 };
}

export function readCheckpoint(id) {
    const item = checkpoints.get(id);
    if (!item?.bytes) throw new Error("Checkpoint reader is unavailable.");
    const bytes = item.bytes.slice(item.offset, item.offset + chunkSize);
    item.offset += bytes.length;
    return bytes;
}

export function appendCheckpoint(id, bytes) {
    const item = checkpoints.get(id);
    if (!item || item.bytes || bytes.length > chunkSize || item.size + bytes.length > maxCheckpointBytes)
        throw new Error("Conversation exceeds the tab checkpoint limit. Start a new conversation.");
    item.parts.push(bytes);
    item.size += bytes.length;
}

export function commitCheckpoint(id) {
    const item = checkpoints.get(id);
    if (!item || item.bytes) throw new Error("Checkpoint writer is unavailable.");
    const bytes = new Uint8Array(item.size);
    let at = 0;
    for (const part of item.parts) {
        bytes.set(part, at);
        at += part.length;
    }
    save(item.route, new TextDecoder("utf-8", { fatal: true }).decode(bytes));
}

export function endCheckpoint(id) { checkpoints.delete(id); }
export function diagnostics() { return { requests: requests.size, checkpoints: checkpoints.size }; }
addEventListener("pagehide", () => {
    for (const id of requests.keys()) abort(id);
    checkpoints.clear();
});

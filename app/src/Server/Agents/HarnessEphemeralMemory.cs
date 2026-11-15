using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Agents;

// One instance belongs to one run; never register this store as a shared singleton.
internal static class HarnessEphemeralMemory
{
    internal static AIAgent Create(IChatClient client) => client.AsHarnessAgent(new HarnessAgentOptions
    {
        Name = "MemoryAssistant",
        DisableTodoProvider = true,
        DisableAgentModeProvider = true,
        DisableAgentSkillsProvider = true,
        DisableWebSearch = true,
        DisableToolAutoApproval = true,
        DisableOpenTelemetry = true,
        FileMemoryStore = new BoundedMemoryStore(),
        MaximumIterationsPerRequest = 4,
        ChatOptions = new ChatOptions { Instructions = "Use temporary memory tools when useful. Memory is available only during this run and is not recalled in future runs. Do not request or store secrets." }
    });

    internal sealed class BoundedMemoryStore : AgentFileStore
    {
        private const int MaxFiles = 64;
        private const int MaxCharacters = 131072;
        private readonly InMemoryAgentFileStore _inner = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, int> _sizes = new(StringComparer.Ordinal);
        private int _total;

        public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ValidatePath(path);
                int previous = _sizes.GetValueOrDefault(path);
                if (path.Length > 512 || (!_sizes.ContainsKey(path) && _sizes.Count >= MaxFiles)
                    || content.Length > MaxCharacters - (_total - previous))
                    throw new InvalidOperationException("Temporary memory limit exceeded.");
                await _inner.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
                _sizes[path] = content.Length;
                _total += content.Length - previous;
            }
            finally { _gate.Release(); }
        }

        public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ValidatePath(path);
                bool deleted = await _inner.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
                if (deleted && _sizes.Remove(path, out int size)) _total -= size;
                return deleted;
            }
            finally { _gate.Release(); }
        }
        public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await _inner.ReadAsync(path, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await _inner.FileExistsAsync(path, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await _inner.ListChildrenAsync(directory, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => _inner.CreateDirectoryAsync(path, cancellationToken);

        private static void ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/')
                || path.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new ArgumentException("Memory paths must be canonical relative paths.", nameof(path));
        }
    }
}

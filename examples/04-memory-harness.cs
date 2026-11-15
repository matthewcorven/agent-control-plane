#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:include common.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--memory" || !Directory.Exists(args[1])))
{
    Console.Error.WriteLine("Usage: dotnet run examples/04-memory-harness.cs -- [--memory <existing-directory>]");
    return;
}
var persistent = args.Length == 2;
var path = persistent ? Path.GetFullPath(args[1]) : null;
Console.WriteLine(persistent ? $"Opt-in file memory: {path}\nFiles persist on disk, but each run starts a new memory session; this example does not restore or recall earlier sessions.\nMemory is plaintext, not encrypted. Do not enter secrets. Every change requires review." : "Session history is in memory only; no persistent memory. Do not enter secrets.");
Console.WriteLine("File-store scoping is not an OS sandbox. Use a trusted, private directory; no shell or web tools are enabled.");
var endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
using var http = LocalHttp.Create(endpoint);
var client = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions { Endpoint = endpoint, Transport = new HttpClientPipelineTransport(http) });
var options = new HarnessAgentOptions
{
    DisableTodoProvider = true, DisableAgentModeProvider = true, DisableFileMemory = !persistent,
    DisableAgentSkillsProvider = true, DisableWebSearch = true, DisableToolAutoApproval = true,
    DisableOpenTelemetry = true,
    ChatOptions = new ChatOptions { Instructions = "Use session context to continue the conversation. Never request or store secrets. Memory files are scoped to this session; do not promise recall after a restart or in future sessions." }
};
if (persistent) options.FileMemoryStore = new ReviewedMemoryStore(path!);
var agent = client.GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient().AsHarnessAgent(options);
var session = await agent.CreateSessionAsync();
while (true)
{
    Console.Write("You (empty to exit): ");
    var line = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(line)) break;
    try
    {
        var response = await agent.RunAsync(line, session);
        Console.WriteLine(response.Text);
    }
    catch (MemoryChangeDeniedException)
    {
        Console.WriteLine("Memory change denied; the turn could not be completed. You may try another turn.");
    }
}

sealed class MemoryChangeDeniedException() : UnauthorizedAccessException("Memory change denied.");

sealed class ReviewedMemoryStore(string root) : AgentFileStore
{
    private readonly FileSystemAgentFileStore inner = new(root);
    private static void Review(string action, string path, string? content = null)
    {
        Console.WriteLine($"\nMemory {action}: {path}");
        if (content is not null) Console.WriteLine($"Proposed content:\n{content}\nEnd proposed content.");
        Console.Write("Approve this exact change? Type YES: ");
        if (Console.IsInputRedirected || Console.ReadLine() != "YES") throw new MemoryChangeDeniedException();
    }
    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    { Review("write", path, content); return inner.WriteAsync(path, content, cancellationToken); }
    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    { Review("delete", path); return inner.DeleteAsync(path, cancellationToken); }
    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default) => inner.ReadAsync(path, cancellationToken);
    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default) => inner.ListChildrenAsync(directory, cancellationToken);
    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) => inner.FileExistsAsync(path, cancellationToken);
    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    { Review("create directory", path); return inner.CreateDirectoryAsync(path, cancellationToken); }
}

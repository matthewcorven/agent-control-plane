#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package Microsoft.Agents.AI.OpenAI@1.28.0
#:property PublishAot=false
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

if (Console.IsInputRedirected)
{
    Console.Error.WriteLine("An interactive terminal is required for the API key and prompt.");
    return 1;
}
Console.WriteLine("This sends your prompt to OpenRouter and its selected provider; charges and their data policies may apply.");
Console.Write("OpenRouter model ID: ");
var model = Console.ReadLine()?.Trim();
if (string.IsNullOrEmpty(model) || model.Length > 200 || model.Any(char.IsControl))
{
    Console.Error.WriteLine("A valid model ID is required.");
    return 1;
}
Console.Write("API key (input hidden): ");
var key = new StringBuilder();
while (true)
{
    var character = Console.ReadKey(intercept: true);
    if (character.Key == ConsoleKey.Enter) break;
    if (character.Key == ConsoleKey.Backspace) { if (key.Length > 0) key.Length--; continue; }
    if (!char.IsControl(character.KeyChar) && key.Length < 1024) key.Append(character.KeyChar);
}
Console.WriteLine();
if (key.Length == 0) { Console.Error.WriteLine("No key provided."); return 1; }
Console.Write("Prompt: ");
var prompt = Console.ReadLine();
if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 4000)
{
    Console.Error.WriteLine("A prompt of 1–4000 characters is required.");
    return 1;
}
using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
var client = new OpenAIClient(new ApiKeyCredential(key.ToString()), new OpenAIClientOptions
{
    Endpoint = new Uri("https://openrouter.ai/api/v1"),
    Transport = new HttpClientPipelineTransport(http)
});
key.Clear();
var agent = client.GetChatClient(model).AsIChatClient().AsHarnessAgent(new HarnessAgentOptions
{
    Name = "RemoteAssistant",
    DisableFileMemory = true, DisableAgentSkillsProvider = true,
    DisableWebSearch = true, DisableToolAutoApproval = true, DisableOpenTelemetry = true,
    MaximumIterationsPerRequest = 4,
    ChatOptions = new ChatOptions { MaxOutputTokens = 512, Instructions = "Be concise and helpful." }
});
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    Console.WriteLine((await agent.RunAsync(prompt, cancellationToken: timeout.Token)).Text);
}
catch (Exception)
{
    // Provider errors can contain request details; do not print them or the credential.
    Console.Error.WriteLine("The request failed or timed out. Check the model, key, account and network settings.");
    return 1;
}
return 0;

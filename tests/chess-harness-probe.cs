#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package Microsoft.Agents.AI.OpenAI@1.28.0
#:property PublishAot=false
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;

var endpoint = new Uri("http://127.0.0.1:8001/v1");
var chat = new OpenAIClient(new ApiKeyCredential("local-no-auth"), new OpenAIClientOptions { Endpoint = endpoint })
    .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
var agent = chat.AsHarnessAgent(new HarnessAgentOptions
{
    Name = "ChessProbe",
    DisableTodoProvider = true,
    DisableAgentModeProvider = true,
    DisableFileMemory = true,
    DisableAgentSkillsProvider = true,
    DisableWebSearch = true,
    DisableToolAutoApproval = true,
    ChatOptions = new ChatOptions { Instructions = "Respond concisely." }
});
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
var response = await agent.RunAsync("Reply with exactly e2e4", cancellationToken: timeout.Token);
if (string.IsNullOrWhiteSpace(response.Text) || !response.Text.Contains("e2e4", StringComparison.Ordinal))
    throw new Exception("Harness did not return the expected move.");
Console.WriteLine("Harness local-model probe passed.");

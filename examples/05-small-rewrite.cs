#!/usr/bin/env dotnet
#:include common.cs
var text = await Console.In.ReadToEndAsync();
if (string.IsNullOrWhiteSpace(text)) { Console.Error.WriteLine("Pipe text on stdin."); return; }
Console.WriteLine(await Agent.Ask(text, "Qwen2.5-0.5B-Instruct-4bit", "Rewrite the text clearly and concisely. Return only the rewritten text."));

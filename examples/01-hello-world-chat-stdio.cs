#!/usr/bin/env dotnet
#:include common.cs
var prompt = await Console.In.ReadToEndAsync();
if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("Pipe a prompt on stdin."); return; }
Console.WriteLine(await Agent.Ask(prompt));

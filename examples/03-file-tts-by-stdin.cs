#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 1) { Console.Error.WriteLine("Usage: printf 'prompt' | dotnet examples/03-file-tts-by-stdin.cs <output.wav>"); return 2; }
var prompt = await Console.In.ReadToEndAsync();
if (string.IsNullOrWhiteSpace(prompt)) { Console.Error.WriteLine("Prompt is empty."); return 2; }
await Audio.Speak(await Agent.Ask(prompt), args[0]);
return 0;

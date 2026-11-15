#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet examples/03-file-tts-by-prompt.cs <output.wav> <prompt>"); return 2; }
await Audio.Speak(await Agent.Ask(args[1]), args[0]);
return 0;

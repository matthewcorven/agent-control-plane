#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet examples/03-file-audio-and-tts.cs <input.wav> <output.wav>"); return 2; }
var transcript = await Audio.Transcribe(args[0]);
Console.Error.WriteLine($"Transcript: {transcript}");
await Audio.Speak(await Agent.Ask(transcript), args[1]);
return 0;

#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 2 || !File.Exists(args[0])) { Console.Error.WriteLine("Usage: dotnet examples/03-file-append.cs <existing-file> <prompt>"); return 2; }
await File.AppendAllTextAsync(args[0], Environment.NewLine + await Agent.Ask(args[1]));
return 0;

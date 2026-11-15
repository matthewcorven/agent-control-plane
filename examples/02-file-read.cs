#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 1) { Console.Error.WriteLine("Usage: dotnet examples/02-file-read.cs <file>"); return 2; }
Console.WriteLine(await Agent.Ask("Summarize this user-provided text:\n" + await File.ReadAllTextAsync(args[0])));
return 0;

#!/usr/bin/env dotnet
#:include common.cs
if (args.Length != 2) { Console.Error.WriteLine("Usage: dotnet examples/03-file-write.cs <new-file> <prompt>"); return 2; }
var result = await Agent.Ask(args[1]);
await using var file = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write);
await using var writer = new StreamWriter(file);
await writer.WriteAsync(result);
return 0;

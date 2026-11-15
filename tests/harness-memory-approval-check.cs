#!/usr/bin/env dotnet
#:package Microsoft.Agents.AI.Harness@1.28.0
#pragma warning disable MAAI001
using System.Reflection;
using Microsoft.Agents.AI;

if (args.Length != 2) throw new ArgumentException("Supply the built memory example assembly and an existing disposable directory.");
var type = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType("ReviewedMemoryStore", throwOnError: true)!;
var store = (AgentFileStore)Activator.CreateInstance(type, Path.GetFullPath(args[1]))!;
var file = Path.Combine(args[1], "approval-check.txt");
if (File.Exists(file)) throw new InvalidOperationException("Test path already exists.");
try
{
    try { await store.WriteAsync("approval-check.txt", "example data"); throw new Exception("Unapproved write succeeded"); }
    catch (UnauthorizedAccessException) { }
    if (File.Exists(file)) throw new Exception("Denied write created a file");
    await File.WriteAllTextAsync(file, "original");
    try { await store.DeleteAsync("approval-check.txt"); throw new Exception("Unapproved delete succeeded"); }
    catch (UnauthorizedAccessException) { }
    if (await File.ReadAllTextAsync(file) != "original") throw new Exception("Denied delete changed file");
    Console.WriteLine("Denied write and delete left files unchanged.");
}
finally { if (File.Exists(file)) File.Delete(file); }

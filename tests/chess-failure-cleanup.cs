#!/usr/bin/env dotnet
using System.Diagnostics;
using System.Text.RegularExpressions;

var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
foreach (var arg in new[] { "examples/chess.cs", "--", "--headless", "--plies", "2" }) start.ArgumentList.Add(arg);
using var app = Process.Start(start) ?? throw new Exception("Could not start chess app");
var stdout = app.StandardOutput.ReadToEndAsync();
var children = new List<int>();
var stderr = Task.Run(async () =>
{
    while (await app.StandardError.ReadLineAsync() is { } line)
    {
        var match = Regex.Match(line, @"^(White|Black) child=(\d+) parent=(\d+)");
        if (match.Success)
        {
            children.Add(int.Parse(match.Groups[2].Value));
            if (children.Count == 2)
            {
                using var white = Process.GetProcessById(children[0]);
                white.Kill();
            }
        }
    }
});
try
{
    await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(120));
    await stderr;
    await stdout;
    if (children.Count != 2 || app.ExitCode == 0) throw new Exception("Worker failure was not surfaced");
    foreach (int pid in children)
    {
        try { using var child = Process.GetProcessById(pid); if (!child.HasExited) throw new Exception($"Child {pid} survived worker failure"); }
        catch (ArgumentException) { }
    }
    Console.WriteLine("Worker failure surfaced and both child processes exited.");
}
finally
{
    if (!app.HasExited) { app.Kill(entireProcessTree: true); await app.WaitForExitAsync(); }
}

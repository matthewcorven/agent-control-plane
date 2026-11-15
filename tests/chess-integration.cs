#!/usr/bin/env dotnet
using System.Diagnostics;
using System.Text.RegularExpressions;

var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
foreach (var arg in new[] { "examples/chess.cs", "--", "--headless", "--plies", "2" }) start.ArgumentList.Add(arg);
using var app = Process.Start(start) ?? throw new Exception("Could not start chess app");
var output = app.StandardOutput.ReadToEndAsync();
var workers = new Dictionary<string, int>();
var received = new HashSet<string>();
var stderr = Task.Run(async () =>
{
    while (await app.StandardError.ReadLineAsync() is { } line)
    {
        var m = Regex.Match(line, @"^(White|Black) child=(\d+) parent=(\d+)");
        if (m.Success)
        {
            var child = int.Parse(m.Groups[2].Value);
            var reportedParent = int.Parse(m.Groups[3].Value);
            var ps = Process.Start(new ProcessStartInfo("ps") { ArgumentList = { "-o", "ppid=", "-p", child.ToString() }, RedirectStandardOutput = true })!;
            var parent = (await ps.StandardOutput.ReadToEndAsync()).Trim();
            await ps.WaitForExitAsync();
            if (parent != reportedParent.ToString()) throw new Exception($"Worker {child} is not a direct child of {reportedParent}");
            workers.Add(m.Groups[1].Value, child);
        }
        var request = Regex.Match(line, @"^A2A request received by (White|Black) pid=(\d+)$");
        if (request.Success && workers.TryGetValue(request.Groups[1].Value, out var pid) && pid == int.Parse(request.Groups[2].Value))
            received.Add(request.Groups[1].Value);
        Console.Error.WriteLine(line);
    }
});
try
{
    await app.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(180));
    await stderr;
    var text = await output;
    if (app.ExitCode != 0 || workers.Count != 2 || received.Count != 2 || !text.Contains("ply 2") || !text.Contains("Move limit reached"))
        throw new Exception($"Integration failure, exit={app.ExitCode}, workers={workers.Count}, requests={received.Count}\n{text}");
    foreach (var pid in workers.Values)
    {
        try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new Exception($"Worker {pid} survived cleanup"); }
        catch (ArgumentException) { }
    }
    Console.WriteLine("Real model, A2A, direct child and cleanup checks passed.");
}
finally
{
    if (!app.HasExited) { app.Kill(entireProcessTree: true); await app.WaitForExitAsync(); }
}

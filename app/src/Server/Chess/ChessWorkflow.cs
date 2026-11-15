using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace AGUIWebChat.Server.Chess;

public sealed record ChessMessage(string Actor, string Direction, string Text);
public sealed record ChessSnapshot(bool Running, string Fen, string BoardStatus, string Coordinator, string White, string Black, IReadOnlyList<ChessMessage> Messages);

// One explicitly started game per server. Only events emitted by the chess process update the snapshot.
public sealed class ChessWorkflow(IConfiguration configuration, IHostApplicationLifetime lifetime) : IDisposable
{
    private const string InitialFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private readonly object gate = new();
    private ChessSnapshot snapshot = new(false, InitialFen, "Not started", "Not started", "Not started", "Not started", []);
    private CancellationTokenSource? cancellation;
    public ChessSnapshot Snapshot { get { lock (gate) return snapshot; } }

    public bool Start(int plies)
    {
        if (!configuration.GetValue<bool>("Chess:Enabled") || plies is < 1 or > 20) return false;
        string? path = configuration["Chess:ScriptPath"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetFileName(path), "chess.cs", StringComparison.Ordinal) || !File.Exists(path)) return false;
        lock (gate)
        {
            if (cancellation is not null) return false;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            cancellation.CancelAfter(TimeSpan.FromMinutes(5));
            snapshot = new(true, InitialFen, "Starting", "Starting process", "Not started", "Not started", []);
            _ = RunAsync(path, plies, cancellation);
            return true;
        }
    }
    public void Stop() { lock (gate) cancellation?.Cancel(); }
    private async Task RunAsync(string path, int plies, CancellationTokenSource run)
    {
        await Task.Yield();
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(path)! };
            foreach (var arg in new[] { path, "--", "--json", "--plies", plies.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start chess process");
            // Drain diagnostics without publishing process output or environment data.
            var drain = DrainAsync(process.StandardError);
            using var registration = run.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
            while (await process.StandardOutput.ReadLineAsync(run.Token) is { } line)
            {
                if (line.Length > 4096) throw new InvalidOperationException("Invalid chess event");
                if (line.StartsWith('{')) ApplyEvent(line);
            }
            await process.WaitForExitAsync();
            await drain;
            lock (gate) snapshot = snapshot with { BoardStatus = process.ExitCode == 0 ? snapshot.BoardStatus : "Chess process failed", Coordinator = process.ExitCode == 0 ? snapshot.Coordinator : "Process failed" };
        }
        catch (OperationCanceledException) { lock (gate) snapshot = snapshot with { BoardStatus = "Stopped or timed out", Coordinator = "Stopped" }; }
        catch (Exception) { lock (gate) snapshot = snapshot with { BoardStatus = "Chess process failed to start or stopped unexpectedly", Coordinator = "Failed" }; }
        finally { lock (gate) { snapshot = snapshot with { Running = false }; cancellation = null; } run.Dispose(); }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer) != 0) { }
    }
    internal void ApplyEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        string? kind = root.GetProperty("kind").GetString();
        string? status = root.GetProperty("status").GetString();
        lock (gate)
        {
            if (kind == "transcript" && root.TryGetProperty("actor", out var actorElement) &&
                actorElement.GetString() is "Coordinator" or "White" or "Black" &&
                status is "Request" or "Response" && root.TryGetProperty("text", out var textElement) &&
                textElement.GetString() is { Length: > 0 and <= 320 } text && snapshot.Messages.Count < 120)
                snapshot = snapshot with { Messages = [.. snapshot.Messages, new ChessMessage(actorElement.GetString()!, status, text)] };
            else if (kind == "board" && root.GetProperty("fen").GetString() is { Length: > 0 and < 128 } fen && status is { Length: < 200 })
                snapshot = snapshot with { Fen = fen, BoardStatus = status };
            else if (kind == "status" && status is { Length: < 200 })
                snapshot = root.GetProperty("actor").GetString() switch
                {
                    "Coordinator" => snapshot with { Coordinator = status },
                    "White" => snapshot with { White = status },
                    "Black" => snapshot with { Black = status },
                    _ => snapshot
                };
        }
    }
    public void Dispose() => Stop();
}

public static class ChessEndpoints
{
    public static IEndpointRouteBuilder MapChessWorkflow(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/chess").AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext.Request;
            var origin = request.Headers.Origin.ToString();
            if (request.Method == "POST" && request.Headers["X-Chess-Request"] != "1") return Results.StatusCode(403);
            if (context.HttpContext.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback)))
                return Results.StatusCode(403);
            return await next(context);
        });
        group.MapGet("/", (ChessWorkflow chess) => chess.Snapshot);
        group.MapPost("/start/{plies:int}", (int plies, ChessWorkflow chess) => chess.Start(plies) ? Results.Accepted() : Results.BadRequest());
        group.MapPost("/stop", (ChessWorkflow chess) => { chess.Stop(); return Results.Accepted(); });
        return endpoints;
    }
}

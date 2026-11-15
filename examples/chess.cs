#!/usr/bin/env dotnet
#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Agents.AI.Harness@1.28.0
#:package Microsoft.Agents.AI.A2A@1.28.0-preview.261106.1
#:package Microsoft.Agents.AI.Hosting.A2A.AspNetCore@1.28.0-preview.261106.1
#:package Terminal.Gui@1.19.0
#:property PublishAot=false
#:include common.cs
#:include chess-rules.cs
#:include chess-attribution.cs
#pragma warning disable MAAI001
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using A2A;
using A2A.AspNetCore;
using ChessDotNet;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using Terminal.Gui;

if (args.Length == 2 && args[0] == "--worker")
{
    await Worker.Run(args[1]);
    return;
}
await ChessApp.Run(args);

static class Agents
{
    private static readonly Uri Endpoint = LocalEndpoint.Create("http://127.0.0.1:8001/v1");
    private static readonly HttpClient Http = LocalHttp.Create(Endpoint);
    public static IChatClient Chat() => new OpenAIClient(new ApiKeyCredential("local-no-auth"),
        new OpenAIClientOptions { Endpoint = Endpoint, Transport = new HttpClientPipelineTransport(Http) })
        .GetChatClient("Qwen3.8-27B-oQ4e-mtp").AsIChatClient();
    public static HarnessAgent Create(string name, string instructions, IList<AIAgent>? children = null) => Chat().AsHarnessAgent(new HarnessAgentOptions
    {
        Name = name, Description = "Chess " + name,
        DisableTodoProvider = true, DisableAgentModeProvider = true, DisableFileMemory = true,
        DisableAgentSkillsProvider = true, DisableWebSearch = true, DisableToolAutoApproval = true,
        BackgroundAgents = children, ChatOptions = new ChatOptions { Instructions = instructions }
    });
}

static class Worker
{
    public static async Task Run(string color)
    {
        if (color is not ("White" or "Black")) throw new ArgumentException("Invalid player");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var player = Agents.Create(color, "You play chess. Given a position and a list of legal UCI moves, choose one and reply with only that move.");
        builder.Services.AddKeyedSingleton<AIAgent>(color, player);
        builder.AddA2AServer(color);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/a2a" && context.Request.Method == "POST")
                Console.Error.WriteLine($"A2A request received by {color} pid={Environment.ProcessId}");
            await next(context);
        });
        app.MapA2AJsonRpc(color, "/a2a");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Console.WriteLine("READY " + address + "/a2a");
        await Console.Out.FlushAsync();
        await app.WaitForShutdownAsync();
    }
}

sealed class A2AMonitor(Uri white, Uri black, MoveAttribution attribution, Action<string, string>? status = null, Action<string, string, string>? transcript = null) : DelegatingHandler(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? color = request.Method == HttpMethod.Post && request.RequestUri == white ? "White" :
            request.Method == HttpMethod.Post && request.RequestUri == black ? "Black" : null;
        int turn = color is null ? 0 : attribution.Start(color);
        if (color is not null)
        {
            status?.Invoke(color, "A2A request sent");
            if (request.Content is not null && ChessTranscript.Text(await request.Content.ReadAsStringAsync(cancellationToken), false) is { } text)
                transcript?.Invoke(color, "Request", text);
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (color is not null)
            {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            attribution.Finish(turn, response.IsSuccessStatusCode, body);
            if (response.IsSuccessStatusCode && ChessTranscript.Text(body, true) is { } text)
                transcript?.Invoke(color, "Response", text);
            status?.Invoke(color, response.IsSuccessStatusCode ? "A2A response received" : "A2A request failed");
        }
        return response;
    }
}

static class ChessApp
{
    public static async Task Run(string[] args)
    {
        bool json = args.Contains("--json", StringComparer.Ordinal);
        bool headless = json || args.Contains("--headless", StringComparer.Ordinal);
        void Event(string kind, string? fen = null, string? actor = null, string? status = null, string? text = null)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { kind, fen, actor, status, text }));
        }
        int maxMoves = 40;
        var maxArg = Array.IndexOf(args, "--plies");
        if (maxArg >= 0 && (maxArg + 1 >= args.Length || !int.TryParse(args[maxArg + 1], out maxMoves) || maxMoves is < 1 or > 200))
            throw new ArgumentException("--plies must be between 1 and 200");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); if (!headless) Application.MainLoop?.Invoke(() => Application.RequestStop()); };
        var children = new List<Process>();
        try
        {
            Event("status", actor: "Coordinator", status: "Starting workers");
            var white = await StartChild("White", children, stop.Token);
            Event("status", actor: "White", status: $"Worker ready (pid {children[^1].Id})");
            var black = await StartChild("Black", children, stop.Token);
            Event("status", actor: "Black", status: $"Worker ready (pid {children[^1].Id})");
            void Transcript(string actor, string direction, string text) => Event("transcript", actor: actor, status: direction, text: ChessTranscript.Bound(text));
            var attribution = new MoveAttribution();
            var monitor = new A2AMonitor(white, black, attribution, (actor, status) => Event("status", actor: actor, status: status), Transcript);
            using var http = new HttpClient(monitor) { Timeout = TimeSpan.FromSeconds(90) };
            AIAgent whiteAgent = new A2AAgent(new A2AClient(white, http), new A2AAgentOptions { Name = "White", Description = "White chess player" });
            AIAgent blackAgent = new A2AAgent(new A2AClient(black, http), new A2AAgentOptions { Name = "Black", Description = "Black chess player" });
            var coordinator = Agents.Create("Coordinator", "You coordinate chess. For every request you MUST delegate the position and legal moves to the named background agent, wait for its task to finish, retrieve its result, and return only its UCI move. Never select a move yourself.", [whiteAgent, blackAgent]);
            var game = new ChessRules();
            if (headless)
                await Play(game, coordinator, attribution, maxMoves, text => { if (!json) Console.WriteLine(text); }, stop.Token, (fen, status) => Event("board", fen, status: status), (actor, status) => Event("status", actor: actor, status: status), Transcript);
            else
            {
                Application.Init();
                var window = new Window("Chess — Esc quits") { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
                var label = new Label("") { X = 1, Y = 1, Width = Dim.Fill(2), Height = Dim.Fill(2) };
                window.Add(label);
                Application.Top.Add(window);
                window.KeyPress += e => { if (e.KeyEvent.Key == Key.Esc) { stop.Cancel(); Application.RequestStop(); e.Handled = true; } };
                label.Text = Render(game.Fen, "Starting");
                var uiLock = new object();
                bool closing = false;
                void Update(string text)
                {
                    lock (uiLock)
                        if (!closing) Application.MainLoop.Invoke(() => label.Text = text);
                }
                var task = Task.Run(async () =>
                {
                    try { await Play(game, coordinator, attribution, maxMoves, Update, stop.Token); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception ex) { Update(Render(game.Fen, "Stopped: " + ex.Message)); }
                });
                Application.Run();
                lock (uiLock) closing = true;
                stop.Cancel();
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { Console.Error.WriteLine("Game task did not stop within five seconds."); }
                Application.Shutdown();
            }
            GC.KeepAlive(coordinator);
        }
        finally
        {
            stop.Cancel();
            foreach (var child in children)
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            foreach (var child in children)
            {
                try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { Console.Error.WriteLine($"Failed to reap child {child.Id}"); }
                child.Dispose();
            }
        }
    }

    private static async Task<Uri> StartChild(string color, List<Process> children, CancellationToken token)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(color);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start worker");
        children.Add(process);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.BeginErrorReadLine();
        var line = await process.StandardOutput.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal) || !Uri.TryCreate(line[6..], UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new InvalidOperationException($"{color} worker did not start: {line}");
        Console.Error.WriteLine($"{color} child={process.Id} parent={Environment.ProcessId} endpoint={uri}");
        return uri;
    }

    private static async Task Play(ChessRules game, AIAgent coordinator, MoveAttribution attribution, int limit, Action<string> update, CancellationToken token, Action<string, string>? board = null, Action<string, string>? status = null, Action<string, string, string>? transcript = null)
    {
        update(Render(game.Fen, "Game started"));
        board?.Invoke(game.Fen, "Game started");
        for (int ply = 0; ply < limit && !game.Checkmate && !game.Stalemate; ply++)
        {
            var legal = game.LegalMoves();
            var player = game.Turn == Player.White ? "White" : "Black";
            var prompt = $"Delegate to {player}: Position FEN: {game.Fen}\nChoose exactly one move from: {string.Join(' ', legal)}. Wait for the result and reply with only that UCI move.";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            attribution.Begin(player);
            status?.Invoke("Coordinator", $"Running turn for {player}");
            transcript?.Invoke("Coordinator", "Request", prompt);
            var response = await coordinator.RunAsync(prompt, cancellationToken: timeout.Token);
            transcript?.Invoke("Coordinator", "Response", response.Text);
            status?.Invoke("Coordinator", "Turn returned; verifying attribution");
            var move = attribution.Complete(response.Text);
            if (!game.TryMove(move)) throw new InvalidOperationException($"{player} returned illegal move: {move}");
            update(Render(game.Fen, $"{player}: {move} (ply {ply + 1})"));
            board?.Invoke(game.Fen, $"{player}: {move} (ply {ply + 1})");
        }
        var result = game.Checkmate ? "Checkmate" : game.Stalemate ? "Stalemate" : "Move limit reached";
        update(Render(game.Fen, result));
        board?.Invoke(game.Fen, result);
        status?.Invoke("Coordinator", "Game complete");
    }

    private static string Render(string fen, string status)
    {
        var glyphs = new Dictionary<char, string> { ['K']="♔", ['Q']="♕", ['R']="♖", ['B']="♗", ['N']="♘", ['P']="♙", ['k']="♚", ['q']="♛", ['r']="♜", ['b']="♝", ['n']="♞", ['p']="♟" };
        var board = new StringBuilder(); int rank = 8;
        foreach (var row in fen.Split(' ')[0].Split('/'))
        {
            board.Append(rank--).Append("  ");
            foreach (var c in row) if (char.IsDigit(c)) for (int i = 0; i < c - '0'; i++) board.Append("· "); else board.Append(glyphs[c]).Append(' ');
            board.AppendLine();
        }
        return board.AppendLine("   a b c d e f g h").AppendLine().Append("⌚ ").Append(status).ToString();
    }
}

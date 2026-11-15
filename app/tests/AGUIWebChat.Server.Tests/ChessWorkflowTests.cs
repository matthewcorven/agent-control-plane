using AGUIWebChat.Server.Chess;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AGUIWebChat.Server.Tests;

public sealed class ChessWorkflowTests
{
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    [Fact]
    public void DefaultConfigurationCannotStartAndInvalidBoundsAreRejected()
    {
        using var workflow = new ChessWorkflow(new ConfigurationBuilder().Build(), new Lifetime());
        Assert.False(workflow.Start(1));
        Assert.False(workflow.Start(0));
        Assert.False(workflow.Start(21));
        Assert.False(workflow.Snapshot.Running);
    }
    [Fact]
    public void SnapshotsReflectActualEventsAndIgnoreUnknownActors()
    {
        using var workflow = new ChessWorkflow(new ConfigurationBuilder().Build(), new Lifetime());
        workflow.ApplyEvent("{\"kind\":\"status\",\"fen\":null,\"actor\":\"White\",\"status\":\"A2A request sent\"}");
        workflow.ApplyEvent("{\"kind\":\"board\",\"fen\":\"8/8/8/8/8/8/8/8 w - - 0 1\",\"actor\":null,\"status\":\"White: e2e4\"}");
        workflow.ApplyEvent("{\"kind\":\"status\",\"fen\":null,\"actor\":\"Other\",\"status\":\"ignored\"}");
        Assert.Equal("A2A request sent", workflow.Snapshot.White);
        Assert.Equal("White: e2e4", workflow.Snapshot.BoardStatus);
        Assert.Equal("8/8/8/8/8/8/8/8 w - - 0 1", workflow.Snapshot.Fen);
        Assert.Equal("Not started", workflow.Snapshot.Black);
    }
    [Fact]
    public void TranscriptEventsAreAttributedAndBounded()
    {
        using var workflow = new ChessWorkflow(new ConfigurationBuilder().Build(), new Lifetime());
        workflow.ApplyEvent("{\"kind\":\"transcript\",\"actor\":\"White\",\"status\":\"Request\",\"text\":\"observed request\"}");
        workflow.ApplyEvent("{\"kind\":\"transcript\",\"actor\":\"Black\",\"status\":\"Response\",\"text\":\"observed response\"}");
        workflow.ApplyEvent("{\"kind\":\"transcript\",\"actor\":\"Other\",\"status\":\"Request\",\"text\":\"ignored\"}");
        workflow.ApplyEvent(System.Text.Json.JsonSerializer.Serialize(new { kind = "transcript", actor = "White", status = "Response", text = new string('x', 321) }));
        var messages = workflow.Snapshot.Messages;
        Assert.Collection(messages,
            m => { Assert.Equal("White", m.Actor); Assert.Equal("Request", m.Direction); Assert.Equal("observed request", m.Text); },
            m => { Assert.Equal("Black", m.Actor); Assert.Equal("Response", m.Direction); Assert.Equal("observed response", m.Text); });
        for (int i = 0; i < 130; i++) workflow.ApplyEvent("{\"kind\":\"transcript\",\"actor\":\"White\",\"status\":\"Request\",\"text\":\"x\"}");
        Assert.Equal(120, workflow.Snapshot.Messages.Count);
    }
    [Fact]
    public async Task RealChessRunWhenExplicitlyConfigured()
    {
        var script = Environment.GetEnvironmentVariable("CHESS_TEST_SCRIPT");
        if (string.IsNullOrEmpty(script)) return;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Chess:Enabled"] = "true", ["Chess:ScriptPath"] = script
        }).Build();
        using var workflow = new ChessWorkflow(configuration, new Lifetime());
        Assert.True(workflow.Start(2));
        Assert.False(workflow.Start(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (workflow.Snapshot.Running) await Task.Delay(200, timeout.Token);
        var result = workflow.Snapshot;
        Assert.Equal("Move limit reached", result.BoardStatus);
        Assert.Equal("Game complete", result.Coordinator);
        Assert.Equal("A2A response received", result.White);
        Assert.Equal("A2A response received", result.Black);
        Assert.Contains(result.Messages, m => m.Actor == "White" && m.Direction == "Request");
        Assert.Contains(result.Messages, m => m.Actor == "White" && m.Direction == "Response");
        Assert.Contains(result.Messages, m => m.Actor == "Black" && m.Direction == "Request");
        Assert.Contains(result.Messages, m => m.Actor == "Black" && m.Direction == "Response");
        Assert.NotEqual("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", result.Fen);
    }
}

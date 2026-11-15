using AGUIWebChat.Client.Components.Chat;
using AGUIWebChat.Client.Formatting;
using AGUIWebChat.Client.Models;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace AGUIWebChat.Client.Tests;

public sealed class ChatMessageComponentTests : BunitContext
{
    public ChatMessageComponentTests() => Services.AddFluentUIComponents();

    [Fact]
    public void EarlyTelemetryOnlyBindsTheExactPreparedRunAndCannotBindAfterReset()
    {
        var first = new ChatMessageModel { Id = "first", Role = ChatRole.Assistant, IsStreaming = true };
        var second = new ChatMessageModel { Id = "second", Role = ChatRole.Assistant, IsStreaming = true };
        var messages = new List<ChatMessageModel> { first };
        Assert.Null(ChatMessageModel.FindForTelemetry(messages, first, "prepared-1", "foreign"));
        Assert.Null(ChatMessageModel.FindForTelemetry(messages, first, null, ""));
        Assert.Null(first.RunId);
        Assert.Same(first, ChatMessageModel.FindForTelemetry(messages, first, "prepared-1", "prepared-1"));
        Assert.Equal("prepared-1", first.RunId);
        first.IsStreaming = false;
        messages.Add(second);
        Assert.Same(first, ChatMessageModel.FindForTelemetry(messages, second, "prepared-2", "prepared-1"));
        Assert.Null(second.RunId);
        Assert.Same(second, ChatMessageModel.FindForTelemetry(messages, second, "prepared-2", "prepared-2"));
        Assert.Equal("prepared-2", second.RunId);
        messages.Clear();
        Assert.Null(ChatMessageModel.FindForTelemetry(messages, second, "prepared-2", "prepared-2"));
        Assert.Null(ChatMessageModel.FindForTelemetry(messages, second, null, "prepared-1"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonReasoningAnswerShowsMonitoringAndHonoursModelInfoFlag(bool showModelInfo)
    {
        var message = new ChatMessageModel
        {
            Id = "answer", Role = ChatRole.Assistant, Content = "Plain answer.",
            MonitoringTelemetry = new("run", "agent", "model", 3, 2, 5, 10, 200,
                null, null, null, null, null, null)
        };
        var cut = Render<ChatMessage>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.MonitoringDisplay, MonitoringDisplayMode.Expanded)
            .Add(p => p.MonitoringSettings, new MonitoringSettings { ShowModelInfo = showModelInfo }));
        Assert.Single(cut.FindAll(".monitoring-container"));
        Assert.Equal(showModelInfo ? 1 : 0, cut.FindAll(".monitoring-model").Count);
        Assert.Contains("Total tokens", cut.Markup);
    }

    [Theory]
    [InlineData("Response stopped.")]
    [InlineData("Error: upstream failed.")]
    [InlineData("Error: <script>alert('unsafe')</script>")]
    public void RichPartialAnswerKeepsCompletionStatusAsEncodedText(string status)
    {
        var message = new ChatMessageModel
        {
            Id = "answer",
            Role = ChatRole.Assistant,
            Content = "## Partial answer\n\nSaved **partial** text.",
            Nodes = MarkdownRichTextParser.Parse("## Partial answer\n\nSaved **partial** text."),
            StatusMessage = status
        };

        var cut = Render<ChatMessage>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.MonitoringSettings, new MonitoringSettings()));

        Assert.Equal("Partial answer", cut.Find(".rt-h2").TextContent);
        Assert.Equal("Saved partial text.", cut.Find("p.rt-p").TextContent);
        Assert.Equal(status, Assert.Single(cut.FindAll(".message-status")).TextContent);
        Assert.Equal("status", cut.Find(".message-status").GetAttribute("role"));
        Assert.Empty(cut.FindAll("script"));
        Assert.Empty(cut.FindAll(".streaming-cursor"));
        Assert.Equal("## Partial answer\n\nSaved **partial** text.", message.Content);
    }

    [Fact]
    public void PlainFallbackAnswerKeepsCompletionStatusSeparate()
    {
        var message = new ChatMessageModel
        {
            Id = "answer",
            Role = ChatRole.Assistant,
            Content = "Plain partial text.",
            StatusMessage = "Response stopped."
        };
        var cut = Render<ChatMessage>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.MonitoringSettings, new MonitoringSettings()));

        Assert.Equal("Plain partial text.", cut.Find(".message-content > p:not(.message-status)").TextContent);
        Assert.Equal("Response stopped.", Assert.Single(cut.FindAll(".message-status")).TextContent);
        Assert.Empty(cut.FindAll(".rt-p"));
    }

    [Fact]
    public void StreamingAnswerHasCursorButNoCompletionStatus()
    {
        var message = new ChatMessageModel
        {
            Id = "answer",
            Role = ChatRole.Assistant,
            Content = "Streaming.",
            Nodes = MarkdownRichTextParser.Parse("Streaming."),
            IsStreaming = true
        };
        var cut = Render<ChatMessage>(parameters => parameters
            .Add(p => p.Message, message)
            .Add(p => p.MonitoringSettings, new MonitoringSettings()));

        Assert.Single(cut.FindAll(".streaming-cursor"));
        Assert.Empty(cut.FindAll(".message-status"));
    }
}

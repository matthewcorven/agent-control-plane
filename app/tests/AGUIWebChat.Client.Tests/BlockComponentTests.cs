using AGUIWebChat.Client.Components.Blocks;
using AGUIWebChat.Client.Models;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace AGUIWebChat.Client.Tests;

/// <summary>
/// Component contracts complement the real scenario transport E2E tests.
/// </summary>
public sealed class BlockComponentTests : BunitContext
{
    public BlockComponentTests()
    {
        Services.AddFluentUIComponents();
    }

    [Fact]
    public void ToolBlock_WhenStreaming_ShouldShowRunningBadgeAndDetail()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.ToolCall,
            State = ChatBlockState.Streaming,
            Title = "search_docs",
            Detail = "{\"query\":\"aspire\"}",
            DurationMs = 1500
        };

        var cut = Render<ToolBlock>(parameters => parameters.Add(p => p.Block, block));

        Assert.Contains("tool-block-streaming", cut.Markup);
        Assert.Contains("Running", cut.Markup);
        Assert.Contains("search_docs", cut.Markup);
        Assert.DoesNotContain("aspire", cut.Markup);
        Assert.DoesNotContain("{&quot;", cut.Markup);
        Assert.Contains("1.5 s", cut.Markup);
    }

    [Fact]
    public void ToolBlock_WhenComplete_ShouldShowToolBadge()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.ToolCall,
            State = ChatBlockState.Complete,
            Title = "search_docs"
        };

        var cut = Render<ToolBlock>(parameters => parameters.Add(p => p.Block, block));

        Assert.Contains("tool-block-complete", cut.Markup);
        Assert.Contains("Tool", cut.Markup);
    }

    [Fact]
    public void ToolBlock_WhenFailed_ShouldShowFailedStateClass()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.ToolCall,
            State = ChatBlockState.Failed,
            Title = "search_docs"
        };

        var cut = Render<ToolBlock>(parameters => parameters.Add(p => p.Block, block));

        Assert.Contains("tool-block-failed", cut.Markup);
    }

    [Fact]
    public void ApprovalBlock_WhenUnresolved_ShouldRenderApproveAndReject()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.Approval,
            Title = "Delete the model?",
            Detail = "{\"modelId\":7}"
        };

        var cut = Render<ApprovalBlock>(parameters => parameters.Add(p => p.Block, block));

        Assert.Contains("Approval required", cut.Markup);
        Assert.Contains("Delete the model?", cut.Markup);
        Assert.Contains("Approve", cut.Markup);
        Assert.Contains("Reject", cut.Markup);
        Assert.False(block.IsResolved);
    }

    [Fact]
    public void ApprovalBlock_WhenApproved_ShouldRaiseDecisionWithoutOptimisticResolution()
    {
        var block = new ChatBlockModel { Kind = ChatBlockKind.Approval, Title = "Proceed?" };
        bool? decision = null;

        var cut = Render<ApprovalBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.OnDecision, (bool accepted) => decision = accepted));

        cut.Find("fluent-button[aria-label='Approve']").Click();

        Assert.False(block.IsResolved);
        Assert.Null(block.Accepted);
        Assert.True(decision);
        Assert.DoesNotContain(">Approved<", cut.Markup);
    }

    [Fact]
    public void ApprovalBlock_WhenRejected_ShouldResolveAsRejected()
    {
        var block = new ChatBlockModel { Kind = ChatBlockKind.Approval, Title = "Proceed?" };
        bool? decision = null;

        var cut = Render<ApprovalBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.OnDecision, (bool accepted) => decision = accepted));

        cut.Find("fluent-button[aria-label='Reject']").Click();

        Assert.False(block.IsResolved);
        Assert.Null(block.Accepted);
        Assert.False(decision);
        Assert.DoesNotContain(">Rejected<", cut.Markup);
    }

    [Fact]
    public void UIActionBlock_WhenAccepted_ShouldResolveAndRaiseDecision()
    {
        var block = new ChatBlockModel { Kind = ChatBlockKind.UIAction, Title = "Apply diff?" };
        bool? decision = null;

        var cut = Render<UIActionBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.OnDecision, (bool accepted) => decision = accepted));

        Assert.Contains("UI action", cut.Markup);

        cut.Find("fluent-button[aria-label='Accept change']").Click();

        Assert.False(block.IsResolved);
        Assert.Null(block.Accepted);
        Assert.True(decision);
        Assert.DoesNotContain(">Accepted<", cut.Markup);
    }

    [Fact]
    public void UIActionBlock_WhenRejected_ShouldResolveAsRejected()
    {
        var block = new ChatBlockModel { Kind = ChatBlockKind.UIAction, Title = "Apply diff?" };
        bool? decision = null;

        var cut = Render<UIActionBlock>(parameters => parameters.Add(p => p.Block, block)
            .Add(p => p.OnDecision, (bool accepted) => decision = accepted));

        cut.Find("fluent-button[aria-label='Reject change']").Click();

        Assert.False(block.IsResolved);
        Assert.Null(block.Accepted);
        Assert.False(decision);
        Assert.DoesNotContain(">Rejected<", cut.Markup);
    }

    [Fact]
    public void ReasoningBlock_WhenHidden_ShouldRenderNothing()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.Reasoning,
            Text = "thinking about it"
        };

    var cut = Render<ReasoningBlock>(parameters => parameters
        .Add(p => p.Block, block)
        .Add(p => p.DisplayMode, ReasoningDisplayMode.Hidden));

    Assert.DoesNotContain("reasoning-block", cut.Markup);
    }

    [Fact]
    public void ReasoningBlock_WhenTextIsBlank_ShouldRenderNothing()
    {
        var block = new ChatBlockModel { Kind = ChatBlockKind.Reasoning, Text = "   " };

        var cut = Render<ReasoningBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.DisplayMode, ReasoningDisplayMode.Expanded));

        Assert.DoesNotContain("reasoning-block", cut.Markup);
    }

    [Fact]
    public void ReasoningBlock_WhenCollapsed_ShouldHideContentUntilToggled()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.Reasoning,
            Text = "step one",
            DurationMs = 250,
            ChunkCount = 3,
            CharacterCount = 8
        };

        var cut = Render<ReasoningBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.DisplayMode, ReasoningDisplayMode.Collapsed));

        Assert.Contains("reasoning-block", cut.Markup);
        Assert.Contains("250 ms", cut.Markup);
        Assert.DoesNotContain("step one", cut.Markup);

        cut.Find("fluent-button[aria-label='Toggle reasoning']").Click();

        Assert.Contains("step one", cut.Markup);
        Assert.Contains("3 chunks", cut.Markup);
        Assert.Contains("8 chars", cut.Markup);
    }

    [Fact]
    public void ReasoningBlock_WhenExpanded_ShouldShowContentImmediately()
    {
        var block = new ChatBlockModel
        {
            Kind = ChatBlockKind.Reasoning,
            Text = "step one",
            DurationMs = 2000
        };

        var cut = Render<ReasoningBlock>(parameters => parameters
            .Add(p => p.Block, block)
            .Add(p => p.DisplayMode, ReasoningDisplayMode.Expanded));

        Assert.Contains("step one", cut.Markup);
        Assert.Contains("2.0 s", cut.Markup);
    }
}

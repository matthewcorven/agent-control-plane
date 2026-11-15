using AGUIWebChat.Client.Models;
using Microsoft.AspNetCore.Components.AI;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Client.Formatting;

public sealed class ScenarioToolContentBlock(ScenarioToolModel model) : ContentBlock
{
    public ScenarioToolModel Model { get; } = model;
}

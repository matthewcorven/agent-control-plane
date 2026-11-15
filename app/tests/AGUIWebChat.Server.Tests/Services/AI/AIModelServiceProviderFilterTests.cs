using AGUIWebChat.Server.Domain.AI;
using AGUIWebChat.Server.Services.AI;
using AGUIWebChat.Server.Tests.Infrastructure;
using Xunit;

namespace AGUIWebChat.Server.Tests.Services.AI;

public class AIModelServiceProviderFilterTests
{
    [Fact]
    public async Task EnabledOnlyHidesModelsOfDisabledProviders()
    {
        await using var factory = await TestDbContextFactory.CreateAsync(TestContext.Current.CancellationToken);

        var disabledProvider = new AIProvider
        {
            Name = "Disabled provider",
            ProviderType = "Ollama",
            IsEnabled = false
        };

        factory.DbContext.AIProviders.Add(disabledProvider);
        await factory.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        factory.DbContext.AIModels.AddRange(
            new AIModel
            {
                ProviderId = disabledProvider.Id,
                ModelId = "hidden-model",
                DisplayName = "Hidden Model",
                IsEnabled = true
            },
            new AIModel
            {
                ProviderId = 1,
                ModelId = "visible-model",
                DisplayName = "Visible Model",
                IsEnabled = true
            });

        await factory.DbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new AIModelService(factory.DbContext);

        var enabled = await service.GetModelsAsync(enabledOnly: true, TestContext.Current.CancellationToken);
        var all = await service.GetModelsAsync(enabledOnly: false, TestContext.Current.CancellationToken);

        Assert.Equal(["Visible Model"], enabled.Select(x => x.DisplayName).ToArray());
        Assert.Equal(2, all.Count);
    }
}

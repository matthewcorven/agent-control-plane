using AGUIWebChat.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AGUIWebChat.Server.Tests.Infrastructure;

public class DatabaseFixtureTests
{
    [Fact]
    public async Task ScopesOwnConnectionsAndFactoriesOwnIsolatedCataloguesAndFiles()
    {
        string databasePath;
        using (var factory = new AGUIWebChatWebApplicationFactory())
        using (var otherFactory = new AGUIWebChatWebApplicationFactory())
        {
            using var firstScope = factory.Services.CreateScope();
            using var secondScope = factory.Services.CreateScope();
            using var otherScope = otherFactory.Services.CreateScope();
            var first = firstScope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var second = secondScope.ServiceProvider.GetRequiredService<ChatDbContext>();
            var other = otherScope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.NotSame(first.Database.GetDbConnection(), second.Database.GetDbConnection());
            databasePath = first.Database.GetDbConnection().DataSource;
            Assert.Equal(databasePath, second.Database.GetDbConnection().DataSource);
            Assert.NotEqual(databasePath, other.Database.GetDbConnection().DataSource);
            Assert.True(File.Exists(databasePath));

            var provider = await first.AIProviders.SingleAsync(TestContext.Current.CancellationToken);
            provider.Name = "This factory only";
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(provider.Name, await second.AIProviders.Select(p => p.Name).SingleAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Ollama", await other.AIProviders.Select(p => p.Name).SingleAsync(TestContext.Current.CancellationToken));
        }

        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
    }

    [Fact]
    public async Task HeldCatalogueReadDoesNotBlockOrJoinAnotherScopesCommit()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var factory = new AGUIWebChatWebApplicationFactory();
        using var readerScope = factory.Services.CreateScope();
        var reader = readerScope.ServiceProvider.GetRequiredService<ChatDbContext>();
        await reader.Database.OpenConnectionAsync(cancellation);
        var connection = Assert.IsType<SqliteConnection>(reader.Database.GetDbConnection());
        using var snapshot = connection.BeginTransaction(deferred: true);
        await reader.Database.UseTransactionAsync(snapshot, cancellation);
        Assert.Equal("Ollama", await reader.AIProviders.Select(p => p.Name).SingleAsync(cancellation));

        await Task.Run(async () =>
        {
            using var writerScope = factory.Services.CreateScope();
            var writer = writerScope.ServiceProvider.GetRequiredService<ChatDbContext>();
            Assert.Equal(1, await writer.AIProviders.ExecuteUpdateAsync(
                update => update.SetProperty(p => p.Name, "Committed independently"), cancellation));
        }, cancellation);

        Assert.Equal("Ollama", await reader.AIProviders.Select(p => p.Name).SingleAsync(cancellation));
        using var observerScope = factory.Services.CreateScope();
        var observer = observerScope.ServiceProvider.GetRequiredService<ChatDbContext>();
        Assert.Equal("Committed independently", await observer.AIProviders.Select(p => p.Name).SingleAsync(cancellation));
        await snapshot.RollbackAsync(cancellation);
        Assert.Equal("Committed independently", await observer.AIProviders.Select(p => p.Name).SingleAsync(cancellation));
    }
}

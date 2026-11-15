using AGUIWebChat.Server;
using AGUIWebChat.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using AGUIWebChat.Server.Inference;
using Microsoft.Extensions.AI;

namespace AGUIWebChat.Server.Tests.Infrastructure
{
    public sealed class AGUIWebChatWebApplicationFactory : WebApplicationFactory<AGUIWebChatServerMarker>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aguiwebchat-tests-{Guid.NewGuid():N}.db");
        private readonly Func<IChatClient>? _inferenceFactory;
        private readonly Action<IServiceCollection>? _configureServices;

        public AGUIWebChatWebApplicationFactory(Func<IChatClient>? inferenceFactory = null, Action<IServiceCollection>? configureServices = null)
        {
            _inferenceFactory = inferenceFactory;
            _configureServices = configureServices;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Catalogue tests never call inference; do not inherit a developer's Ollama endpoint.
                    ["OLLAMA_ENDPOINT"] = "http://127.0.0.1:1",
                    ["OLLAMA_MODEL"] = "catalog-test-model",
                    ["E2E:StubInference"] = (_inferenceFactory is not null).ToString()
                }));

            builder.ConfigureServices(services =>
            {
                // Each scope needs its own connection; EnsureCreated enables WAL for concurrent readers.
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Pooling = false
                }.ToString();
                services.AddDbContext<ChatDbContext>(options =>
                {
                    options.UseSqlite(connectionString);
                });

                if (_inferenceFactory is not null)
                {
                    services.AddScoped<IChatClientFactory>(provider => new ChatClientFactory(
                        provider.GetRequiredService<ChatDbContext>(),
                        provider.GetRequiredService<IConfiguration>(),
                        stubFactory: _inferenceFactory));
                }

                _configureServices?.Invoke(services);

                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

                dbContext.Database.EnsureCreated();
            });
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    File.Delete(_databasePath);
                    File.Delete(_databasePath + "-wal");
                    File.Delete(_databasePath + "-shm");
                }
            }
        }
    }
}

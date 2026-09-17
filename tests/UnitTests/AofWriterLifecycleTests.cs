using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Persistence.Aof;
using UnitTests.Helpers;

namespace UnitTests;

/// <summary>
/// Verifies that the AOF writer that gets started is the same instance the command layer appends
/// through, and that it releases the log file on shutdown.
/// </summary>
public sealed class AofWriterLifecycleTests : IAsyncLifetime
{
    private string _dataDirectory = null!;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "novadb-aof-life", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task StartedWriter_IsTheSameInstanceUsedAsMutationSink()
    {
        var provider = TestHostFactory.CreatePersistent(_dataDirectory);
        try
        {
            var singleton = provider.GetRequiredService<AofWriter>();
            var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                .OfType<AofWriter>()
                .ToList();

            hosted.Should().HaveCount(1, "a second AofWriter instance would be started but never written to");
            hosted[0].Should().BeSameAs(singleton);
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    [Fact]
    public async Task Shutdown_ReleasesTheLogFile()
    {
        var provider = TestHostFactory.CreatePersistent(_dataDirectory);
        await CommandTestHelper.ExecuteAsync(provider, new FakeClientConnection(), "SET", "k", "v");
        await TestHostFactory.DisposeAsync(provider);

        var open = () =>
        {
            using var stream = new FileStream(
                Path.Combine(_dataDirectory, "appendonly.aof"),
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);
            return stream.Length;
        };

        open.Should().NotThrow("the log file must not stay locked after shutdown");
        open().Should().BeGreaterThan(0, "the SET command should have reached the log");
    }

    [Fact]
    public async Task RestartingTwiceOverTheSameDirectory_Succeeds()
    {
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var provider = TestHostFactory.CreatePersistent(_dataDirectory);
            try
            {
                await CommandTestHelper.ExecuteAsync(provider, new FakeClientConnection(), "SET", $"k{cycle}", "v");
            }
            finally
            {
                await TestHostFactory.DisposeAsync(provider);
            }
        }
    }
}

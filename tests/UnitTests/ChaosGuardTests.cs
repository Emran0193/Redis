using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaDB.Chaos;
using NovaDB.Configuration;

namespace UnitTests;

/// <summary>Guards that chaos injection stays Development-gated.</summary>
public sealed class ChaosGuardTests
{
    [Fact]
    public void Enable_Throws_WhenNotDevelopment()
    {
        var engine = CreateEngine(chaosEnabled: true, environmentName: Environments.Production);
        var act = () => engine.Enable(ChaosFaultKind.NetworkLatency, "25");
        act.Should().Throw<InvalidOperationException>();
        engine.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Enable_Throws_WhenChaosDisabledEvenInDevelopment()
    {
        var engine = CreateEngine(chaosEnabled: false, environmentName: Environments.Development);
        var act = () => engine.Enable(ChaosFaultKind.PacketLoss);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task BeforeNetworkRead_AppliesLatency_WhenActiveInDevelopment()
    {
        var engine = CreateEngine(chaosEnabled: true, environmentName: Environments.Development);
        engine.Enable(ChaosFaultKind.NetworkLatency, "20");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await engine.BeforeNetworkReadAsync(CancellationToken.None);
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(15);
        engine.Clear();
        engine.IsActive.Should().BeFalse();
    }

    private static ChaosFaultEngine CreateEngine(bool chaosEnabled, string environmentName)
    {
        var options = Options.Create(new NovaDbOptions { ChaosEnabled = chaosEnabled });
        var env = new HostEnvironmentStub(environmentName);
        return new ChaosFaultEngine(options, env, NullLogger<ChaosFaultEngine>.Instance);
    }

    private sealed class HostEnvironmentStub : IHostEnvironment
    {
        public HostEnvironmentStub(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "NovaDB.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

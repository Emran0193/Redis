using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class AclCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await TestHostFactory.DisposeAsync(_provider);

    [Fact]
    public async Task AclWhoami_ReturnsDefault()
    {
        var response = await CommandTestHelper.ExecuteAsync(_provider, _connection, "ACL", "WHOAMI");
        CommandTestHelper.GetBulkText(response).Should().Be("default");
    }

    [Fact]
    public async Task AclUsers_ListsDefault()
    {
        var response = await CommandTestHelper.ExecuteAsync(_provider, _connection, "ACL", "USERS");
        response.Array!.Select(x => x.AsUtf8String()).Should().Equal("default");
    }
}

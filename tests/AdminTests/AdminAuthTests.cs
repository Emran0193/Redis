using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NovaDB.Admin.Auth;
using Xunit;

namespace AdminTests;

public sealed class AdminAuthTests
{
    [Theory]
    [InlineData("admin", "changeme", AdminRoles.Admin)]
    [InlineData("operator", "changeme", AdminRoles.Operator)]
    [InlineData("readonly", "changeme", AdminRoles.ReadOnly)]
    public void TryAuthenticate_DefaultPasswords_ReturnRoles(string user, string password, string role)
    {
        var config = new ConfigurationBuilder().Build();
        AdminCredentialStore.TryAuthenticate(config, user, password).Should().Be(role);
    }

    [Fact]
    public void TryAuthenticate_WrongPassword_ReturnsNull()
    {
        var config = new ConfigurationBuilder().Build();
        AdminCredentialStore.TryAuthenticate(config, "admin", "nope").Should().BeNull();
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Commands;
using NovaDB.Networking;
using NovaDB.Protocol;

namespace UnitTests.Helpers;

/// <summary>
/// Helpers for dispatching RESP commands through the command processor.
/// </summary>
public static class CommandTestHelper
{
    /// <summary>
    /// Executes a command via <see cref="ICommandProcessor"/>.
    /// </summary>
    public static async Task<RespValue> ExecuteAsync(
        IServiceProvider services,
        IClientConnection connection,
        params string[] parts)
    {
        var processor = services.GetRequiredService<ICommandProcessor>();
        var arguments = parts.Select(RespValue.BulkString).ToArray();
        var request = RespValue.FromArray(arguments);
        return await processor.ProcessAsync(connection, request, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts that a bulk string response equals the expected UTF-8 text.
    /// </summary>
    public static string GetBulkText(RespValue value)
    {
        value.Type.Should().Be(RespType.BulkString);
        value.IsNullBulk.Should().BeFalse();
        return value.AsUtf8String();
    }

    /// <summary>
    /// Asserts that a simple string response equals the expected text.
    /// </summary>
    public static string GetSimpleText(RespValue value)
    {
        value.Type.Should().Be(RespType.SimpleString);
        return value.Simple!;
    }
}

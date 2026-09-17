using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Protocol;

namespace NovaDB.Commands.Internal;

/// <summary>
/// Helpers for validating and reading RESP command arguments.
/// </summary>
internal static class CommandArgumentReader
{
    /// <summary>
    /// Gets the uppercase command name from arguments.
    /// </summary>
    public static string GetCommandName(RespValue[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length == 0)
        {
            throw new NovaDbCommandException("empty command");
        }

        return arguments[0].AsUtf8String().ToUpperInvariant();
    }

    /// <summary>
    /// Ensures at least <paramref name="count"/> arguments including the command name.
    /// </summary>
    public static void RequireMinArgs(string commandName, RespValue[] arguments, int count)
    {
        if (arguments.Length < count)
        {
            throw SyntaxException.WrongNumberOfArguments(commandName);
        }
    }

    /// <summary>
    /// Ensures exactly <paramref name="count"/> arguments including the command name.
    /// </summary>
    public static void RequireExactArgs(string commandName, RespValue[] arguments, int count)
    {
        if (arguments.Length != count)
        {
            throw SyntaxException.WrongNumberOfArguments(commandName);
        }
    }

    /// <summary>
    /// Reads a bulk string argument as raw bytes.
    /// </summary>
    public static byte[] GetBulkBytes(RespValue argument, string paramName)
    {
        if (argument.Type != RespType.BulkString || argument.IsNullBulk || argument.Bulk is null)
        {
            throw new NovaDbCommandException($"{paramName} must be a bulk string");
        }

        return argument.Bulk;
    }

    /// <summary>
    /// Reads a bulk string argument as a Redis key.
    /// </summary>
    public static RedisKey GetKey(RespValue argument, string paramName)
        => new(GetBulkBytes(argument, paramName));

    /// <summary>
    /// Parses an integer argument.
    /// </summary>
    public static long GetInteger(RespValue argument, string paramName)
    {
        if (argument.Type == RespType.Integer)
        {
            return argument.Integer;
        }

        if (argument.Type == RespType.BulkString && !argument.IsNullBulk && argument.Bulk is not null)
        {
            var text = argument.AsUtf8String();
            if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new NovaDbCommandException($"{paramName} is not an integer");
    }

    /// <summary>
    /// Parses a double argument from bulk string or simple string.
    /// </summary>
    public static double GetDouble(RespValue argument, string paramName)
    {
        var text = argument.AsUtf8String();
        if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new NovaDbCommandException($"{paramName} is not a valid float");
    }
}

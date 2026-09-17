using System.Runtime.CompilerServices;

namespace NovaDB.Core.Exceptions;

/// <summary>
/// Base exception for all NovaDB errors that map to RESP error replies.
/// </summary>
public abstract class NovaDbException : Exception
{
    /// <summary>
    /// Gets the RESP error prefix (e.g. ERR, WRONGTYPE, NOAUTH).
    /// </summary>
    public string ErrorPrefix { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NovaDbException"/> class.
    /// </summary>
    /// <param name="errorPrefix">The RESP error prefix.</param>
    /// <param name="message">The error message without prefix.</param>
    protected NovaDbException(string errorPrefix, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ErrorPrefix = errorPrefix;
    }

    /// <summary>
    /// Formats the RESP error payload as "PREFIX message".
    /// </summary>
    public string ToRespError() => $"{ErrorPrefix} {Message}";
}

/// <summary>
/// Generic protocol or command error (RESP ERR).
/// </summary>
public sealed class NovaDbCommandException : NovaDbException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NovaDbCommandException"/> class.
    /// </summary>
    public NovaDbCommandException(string message)
        : base("ERR", message)
    {
    }
}

/// <summary>
/// Raised when an operation is applied to the wrong Redis value type.
/// </summary>
public sealed class WrongTypeException : NovaDbException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WrongTypeException"/> class.
    /// </summary>
    public WrongTypeException()
        : base("WRONGTYPE", "Operation against a key holding the wrong kind of value")
    {
    }
}

/// <summary>
/// Raised when authentication is required but missing or invalid.
/// </summary>
public sealed class AuthenticationException : NovaDbException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthenticationException"/> class.
    /// </summary>
    public AuthenticationException(string message)
        : base("NOAUTH", message)
    {
    }
}

/// <summary>
/// Raised when memory limit is exceeded under noeviction policy.
/// </summary>
public sealed class OutOfMemoryNovaDbException : NovaDbException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutOfMemoryNovaDbException"/> class.
    /// </summary>
    public OutOfMemoryNovaDbException()
        : base("OOM", "command not allowed when used memory > 'maxmemory'")
    {
    }
}

/// <summary>
/// Thrown when a syntax or arity error occurs for a command.
/// </summary>
public sealed class SyntaxException : NovaDbException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SyntaxException"/> class.
    /// </summary>
    public SyntaxException(string message)
        : base("ERR", message)
    {
    }

    /// <summary>
    /// Creates a wrong-number-of-arguments error for the specified command.
    /// </summary>
    public static SyntaxException WrongNumberOfArguments(string commandName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        return new SyntaxException($"wrong number of arguments for '{commandName}' command");
    }
}

/// <summary>
/// Guard helpers for argument validation without allocations in hot paths where possible.
/// </summary>
public static class Guards
{
    /// <summary>
    /// Throws <see cref="ArgumentNullException"/> when <paramref name="value"/> is null.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T NotNull<T>(T? value, string paramName) where T : class
        => value ?? throw new ArgumentNullException(paramName);
}

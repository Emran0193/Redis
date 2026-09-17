using System.Globalization;
using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Converts a mutating command into the form that is safe to store in the append-only log.
/// </summary>
/// <remarks>
/// Relative TTLs must never reach the log: replaying <c>EXPIRE key 60</c> hours after it was issued
/// would grant the key a fresh 60 seconds and resurrect keys that had already expired. Every
/// relative expiry is therefore rewritten to an absolute Unix timestamp at append time.
/// </remarks>
internal static class AofCommandRewriter
{
    /// <summary>
    /// Returns the command to persist, rewriting relative expiry options to absolute ones.
    /// </summary>
    /// <param name="commandName">Uppercase command name.</param>
    /// <param name="arguments">Original command arguments.</param>
    /// <returns>The command array to append.</returns>
    public static RespValue ToDurableForm(string commandName, RespValue[] arguments)
    {
        return commandName switch
        {
            "EXPIRE" => RewriteExpire(arguments, secondsUnit: true),
            "SET" => RewriteSet(arguments),
            "SETEX" => RewriteSetex(arguments, milliseconds: false),
            "PSETEX" => RewriteSetex(arguments, milliseconds: true),
            _ => RespValue.FromArray(arguments)
        };
    }

    private static RespValue RewriteSetex(RespValue[] arguments, bool milliseconds)
    {
        if (arguments.Length != 4 || !TryReadLong(arguments[2], out var ttl) || ttl <= 0)
        {
            return RespValue.FromArray(arguments);
        }

        var expireAt = NowUnixMs() + (milliseconds ? ttl : ttl * 1000L);
        return RespValue.FromArray(
        [
            RespValue.BulkString("SET"),
            arguments[1],
            arguments[3],
            RespValue.BulkString("PXAT"),
            RespValue.BulkString(expireAt.ToString(CultureInfo.InvariantCulture))
        ]);
    }

    private static RespValue RewriteExpire(RespValue[] arguments, bool secondsUnit)
    {
        if (arguments.Length != 3 || !TryReadLong(arguments[2], out var ttl))
        {
            return RespValue.FromArray(arguments);
        }

        var expireAt = NowUnixMs() + (secondsUnit ? ttl * 1000L : ttl);
        return RespValue.FromArray(
        [
            RespValue.BulkString("PEXPIREAT"),
            arguments[1],
            RespValue.BulkString(expireAt.ToString(CultureInfo.InvariantCulture))
        ]);
    }

    private static RespValue RewriteSet(RespValue[] arguments)
    {
        for (var i = 3; i < arguments.Length; i++)
        {
            var option = arguments[i].AsUtf8String().ToUpperInvariant();
            var isSeconds = option == "EX";

            if ((!isSeconds && option != "PX")
                || i + 1 >= arguments.Length
                || !TryReadLong(arguments[i + 1], out var ttl))
            {
                continue;
            }

            var rewritten = (RespValue[])arguments.Clone();
            rewritten[i] = RespValue.BulkString("PXAT");
            rewritten[i + 1] = RespValue.BulkString(
                (NowUnixMs() + (isSeconds ? ttl * 1000L : ttl)).ToString(CultureInfo.InvariantCulture));
            return RespValue.FromArray(rewritten);
        }

        return RespValue.FromArray(arguments);
    }

    private static bool TryReadLong(RespValue value, out long result)
        => long.TryParse(value.AsUtf8String(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

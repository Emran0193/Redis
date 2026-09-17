using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Sessions;

namespace NovaDB.Commands.Security;

/// <summary>
/// Coarse command authorization mapped from session role.
/// Authenticated clients map to <see cref="ClientRole.Operator"/>;
/// unauthenticated clients have <see cref="ClientRole.None"/>.
/// </summary>
public sealed class CommandAuthorization
{
    private readonly IOptions<NovaDbOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandAuthorization"/> class.
    /// </summary>
    public CommandAuthorization(IOptions<NovaDbOptions> options)
        => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Returns whether the session may execute <paramref name="commandName"/>.
    /// Empty password (auth disabled) always allows unless <see cref="NovaDbOptions.RequireAuthForWrites"/>.
    /// </summary>
    public bool IsAllowed(ClientRole role, string commandName, bool isMutating)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        if (!_options.Value.AuthenticationRequired && !_options.Value.RequireAuthForWrites)
        {
            return true;
        }

        if (_options.Value.RequireAuthForWrites && isMutating && role == ClientRole.None)
        {
            return false;
        }

        if (!_options.Value.AuthenticationRequired)
        {
            return true;
        }

        return role switch
        {
            ClientRole.None => !isMutating
                || commandName is "AUTH" or "HELLO" or "PING" or "QUIT" or "COMMAND" or "ACL",
            ClientRole.ReadOnly => !isMutating,
            ClientRole.Operator or ClientRole.Admin => true,
            _ => false
        };
    }

    /// <summary>Maps authentication success to the default operator role.</summary>
    public static ClientRole RoleAfterAuth() => ClientRole.Operator;
}

/// <summary>Append-only audit trail for destructive operations.</summary>
public interface IAuditTrail
{
    /// <summary>Records an audited action.</summary>
    void Record(string actor, string action, string detail);
}

/// <summary>File-backed audit trail under the configured data directory.</summary>
public sealed class FileAuditTrail : IAuditTrail
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly ILogger<FileAuditTrail> _logger;
    private readonly object _gate = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FileAuditTrail"/> class.
    /// </summary>
    public FileAuditTrail(IOptions<NovaDbOptions> options, ILogger<FileAuditTrail> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Record(string actor, string action, string detail)
    {
        var line = $"{DateTimeOffset.UtcNow:o}\t{actor}\t{action}\t{detail}{Environment.NewLine}";
        _logger.LogInformation("AUDIT {Actor} {Action} {Detail}", actor, action, detail);

        var dir = Path.GetFullPath(_options.Value.DataDirectory);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "audit.log");
        lock (_gate)
        {
            File.AppendAllText(path, line);
        }
    }
}

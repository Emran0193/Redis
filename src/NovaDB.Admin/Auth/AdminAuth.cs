namespace NovaDB.Admin.Auth;

/// <summary>Built-in Admin role names.</summary>
public static class AdminRoles
{
    /// <summary>Full configuration access.</summary>
    public const string Admin = "Admin";

    /// <summary>Operational mutations without full config.</summary>
    public const string Operator = "Operator";

    /// <summary>Read-only observers.</summary>
    public const string ReadOnly = "ReadOnly";
}

/// <summary>Authorization policy names.</summary>
public static class AdminPolicies
{
    /// <summary>Any authenticated Admin role.</summary>
    public const string ReadOnly = "ReadOnlyAccess";

    /// <summary>Operator or Admin.</summary>
    public const string Operator = "OperatorAccess";

    /// <summary>Admin only.</summary>
    public const string Admin = "AdminAccess";
}

/// <summary>Resolves demo users from environment / configuration.</summary>
public static class AdminCredentialStore
{
    /// <summary>
    /// Validates credentials and returns the role name, or null when invalid.
    /// </summary>
    public static string? TryAuthenticate(IConfiguration config, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var users = new (string User, string Role, string EnvKey, string ConfigKey)[]
        {
            ("admin", AdminRoles.Admin, "NOVADB_ADMIN_PASSWORD", "AdminAuth:AdminPassword"),
            ("operator", AdminRoles.Operator, "NOVADB_OPERATOR_PASSWORD", "AdminAuth:OperatorPassword"),
            ("readonly", AdminRoles.ReadOnly, "NOVADB_READONLY_PASSWORD", "AdminAuth:ReadOnlyPassword"),
        };

        foreach (var (user, role, envKey, configKey) in users)
        {
            if (!string.Equals(username, user, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var expected = Environment.GetEnvironmentVariable(envKey)
                ?? config[configKey]
                ?? "changeme";
            return string.Equals(password, expected, StringComparison.Ordinal) ? role : null;
        }

        return null;
    }
}

namespace NovaDB.Core.Sessions;

/// <summary>Coarse RESP session role after AUTH.</summary>
public enum ClientRole
{
    /// <summary>No authentication performed.</summary>
    None = 0,

    /// <summary>Read-only observer.</summary>
    ReadOnly = 1,

    /// <summary>Authenticated operator (default after AUTH).</summary>
    Operator = 2,

    /// <summary>Full admin privileges.</summary>
    Admin = 3
}

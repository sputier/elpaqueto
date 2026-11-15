using System.Diagnostics.CodeAnalysis;

namespace ElPaqueto.Security;

/// <summary>
/// What a user or an API key is allowed to do. Values are independent flags: none implies another.
/// </summary>
/// <remarks>
/// The numeric values are persisted in the security database and must never change.
/// Authorization semantics are defined where the flags are enforced.
/// </remarks>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain term used throughout the product; unrelated to System.Security.IPermission.")]
[SuppressMessage("Naming", "CA1714:Flags enums should have plural names", Justification = "Domain term used throughout the product: a user or key has a Permission value made of independent flags.")]
public enum Permission
{
    /// <summary>No permission.</summary>
    None = 0,

    /// <summary>Browse and download packages.</summary>
    Read = 1,

    /// <summary>Publish packages.</summary>
    Publish = 2,

    /// <summary>Administer users and access.</summary>
    Admin = 4,
}

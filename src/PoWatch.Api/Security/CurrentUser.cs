using System.Security.Claims;

namespace PoWatch.Api.Security;

/// <summary>Resolves the stable id that partitions a user's data.</summary>
internal static class CurrentUser
{
    private const string EntraObjectId = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    /// <summary>
    /// Entra's object id when present (stable across apps and sign-ins), otherwise the name
    /// identifier or subject. Null for an anonymous caller.
    /// </summary>
    public static string? Id(ClaimsPrincipal? user) =>
        user?.FindFirst(EntraObjectId)?.Value
        ?? user?.FindFirst("oid")?.Value
        ?? user?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user?.FindFirst("sub")?.Value;

    /// <summary>The address the sign-in carried: preferred_username (OIDC), then email, then upn.</summary>
    public static string? Email(ClaimsPrincipal? user) =>
        user?.FindFirst("preferred_username")?.Value
        ?? user?.FindFirst(ClaimTypes.Email)?.Value
        ?? user?.FindFirst("upn")?.Value;
}

/// <summary>
/// The caller's resolved partition id, bound from <see cref="CurrentUser.Id"/> by Minimal APIs so
/// feature endpoints never repeat the null-check boilerplate.
/// </summary>
internal readonly record struct UserId(string Value)
{
    public static implicit operator string(UserId id) => id.Value;
    public override string ToString() => Value;

    public static ValueTask<UserId> BindAsync(HttpContext context) =>
        CurrentUser.Id(context.User) is { } id
            ? ValueTask.FromResult(new UserId(id))
            : throw new UnauthorizedAccessException("Caller has no user identifier claim.");
}

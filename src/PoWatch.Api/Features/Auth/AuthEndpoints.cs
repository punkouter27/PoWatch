using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using PoWatch.Api.Security;
using PoWatch.Application.Options;
using PoWatch.Shared.Models;

namespace PoWatch.Api.Features.Auth;

/// <summary>
/// BFF auth surface. Environment behaviour:
/// Every environment's sign-in page has one button, Microsoft. Dev/Test hosts with DeveloperBypassAuth also answer
/// the developer URL <c>/auth/login/fake</c> (AGENTS.md); Production answers it 404.
/// </summary>
internal static class AuthEndpoints
{
    internal static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous group: sign-in and state routes must be reachable without an existing session (login
        // can't require prior auth, and /auth/me must report the anonymous state). Authenticated callers
        // still carry their principal — AllowAnonymous only removes the requirement, it doesn't strip claims.
        var group = app.MapGroup("/auth").WithTags("Auth").AllowAnonymous();

        group.MapGet("/me", (ClaimsPrincipal user, string? returnUrl) => TypedResults.Ok(new AuthStateDto(
            IsAuthenticated: user.Identity?.IsAuthenticated ?? false,
            Name: user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.Name),
            // Surface the email/UPN so the navbar can show "you are signed in as …" after Microsoft OAuth.
            // Prefer `preferred_username` (OIDC standard), fall back to `email` and `upn`.
            Email: CurrentUser.Email(user),
            Roles: user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
            ReturnUrl: SafeLocalUrl(returnUrl))))
        .WithName("AuthMe")
        .WithSummary("Server-side authentication state for the client.");

        group.MapGet("/config", (
            IOptions<FeatureFlagsOptions> flags,
            IConfiguration config,
            IWebHostEnvironment env) => TypedResults.Ok(new AuthConfigDto(
                // Microsoft needs a real AzureAd:ClientId; guest sign-in is Dev/Test only.
                MicrosoftEnabled: !string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]),
                GuestEnabled: flags.Value.DeveloperBypassAuth && !env.IsProduction(),
                Environment: env.EnvironmentName)))
        .WithName("AuthConfig")
        .WithSummary("Which sign-in methods are available in this environment.");

        group.MapGet("/login/microsoft", (
            string? returnUrl,
            IConfiguration config,
            IOptions<FeatureFlagsOptions> flags,
            IWebHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]))
            {
                // A local run has no Entra app, but /login shows the Microsoft button everywhere: on a host
                // that allows developer sign-in, the button signs in as the guest instead of dead-ending.
                if (flags.Value.DeveloperBypassAuth && !env.IsProduction())
                    return Results.Redirect($"/auth/login/fake?returnUrl={Uri.EscapeDataString(SafeLocalUrl(returnUrl))}");
                return Results.NotFound(new { message = "Microsoft sign-in is not configured." });
            }

            var props = new AuthenticationProperties { RedirectUri = SafeLocalUrl(returnUrl) };
            return Results.Challenge(props, [OpenIdConnectDefaults.AuthenticationScheme]);
        })
        .WithName("AuthLoginMicrosoft")
        .WithSummary("Begin Microsoft Entra sign-in.");

        group.MapGet("/login/fake", async (
            string? returnUrl,
            string? user,
            string? roles,
            HttpContext http,
            IOptions<FeatureFlagsOptions> flags,
            IWebHostEnvironment env) =>
        {
            if (!flags.Value.DeveloperBypassAuth || env.IsProduction())
                return Results.NotFound(new { message = "Guest sign-in is not available in this environment." });

            // The default guest keeps the same id as a headerless FakeAuth request, so data seeded by
            // automated suites and the signed-in browser belong to one user.
            var id = string.IsNullOrWhiteSpace(user) ? "guest" : user;
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, id),
                new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user) ? "Guest" : user),
                // The Po shell header shows the signed-in email; a guest gets a local, undeliverable one.
                new(ClaimTypes.Email, $"{id.ToLowerInvariant()}@guest.localhost")
            };
            if (!string.IsNullOrWhiteSpace(roles))
                claims.AddRange(roles
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(r => new Claim(ClaimTypes.Role, r)));

            var identity = new ClaimsIdentity(claims, AuthenticationSetup.CookieScheme);
            await http.SignInAsync(AuthenticationSetup.CookieScheme, new ClaimsPrincipal(identity));
            return Results.Redirect(SafeLocalUrl(returnUrl));
        })
        .WithName("AuthLoginFake")
        .WithSummary("Developer sign-in for local and Test hosts (no button; 404 in Production).");

        group.MapPost("/logout", async (HttpContext http, IConfiguration config) =>
        {
            await http.SignOutAsync(AuthenticationSetup.CookieScheme);
            if (!string.IsNullOrWhiteSpace(config["AzureAd:ClientId"]))
                await http.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme);
            return Results.Ok(new { signedOut = true });
        })
        .WithName("AuthLogout")
        .WithSummary("Sign out and clear the session cookie.");

        return app;
    }

    // Prevent open-redirect: only a path on this site is honoured. "//host" and "/\host" are other sites to a
    // browser (it reads a backslash as a slash), and a control character could split the Location header.
    internal static string SafeLocalUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && returnUrl.StartsWith('/')
        && !returnUrl.StartsWith("//", StringComparison.Ordinal)
        && !returnUrl.Contains('\\', StringComparison.Ordinal)
        && !returnUrl.Any(char.IsControl)
            ? returnUrl
            : "/";
}

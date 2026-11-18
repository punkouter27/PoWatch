using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PoWatch.Api.Hosting;
using PoWatch.Application.Options;

namespace PoWatch.Api.Security;

/// <summary>
/// BFF authentication wiring: server-managed HttpOnly, SameSite=Strict encrypted cookie
/// holding the session; interactive sign-in via Microsoft Entra OIDC on the /common endpoint.
/// The Blazor WASM client never sees access tokens — they stay in the encrypted cookie server-side.
/// OIDC is only wired when AzureAd:ClientId is configured, so local/test hosts boot without Azure.
/// </summary>
public static class AuthenticationSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;

    public static bool AddPoWatchAuthentication(this WebApplicationBuilder builder, FeatureFlagsOptions flags)
    {
        var azureAd = builder.Configuration.GetSection("AzureAd");
        var clientId = azureAd["ClientId"];
        var oidcConfigured = !string.IsNullOrWhiteSpace(clientId);

        const string authenticateSelector = "PoWatch.Authenticate";
        var auth = builder.Services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieScheme;
            options.DefaultAuthenticateScheme = authenticateSelector;
            options.DefaultChallengeScheme = oidcConfigured
                ? OpenIdConnectDefaults.AuthenticationScheme
                : CookieScheme;
        });

        auth.AddPolicyScheme(authenticateSelector, "Cookie, Bearer, or explicit X-Fake-User", options =>
        {
            options.ForwardDefaultSelector = ctx =>
            {
                if (oidcConfigured
                    && (ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        || (ctx.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(ctx.Request.Query["access_token"]))))
                    return JwtBearerDefaults.AuthenticationScheme;

                if (flags.DeveloperBypassAuth && ctx.Request.Headers.ContainsKey("X-Fake-User"))
                    return FakeAuthHandler.SchemeName;

                return CookieScheme;
            };
        });

        auth.AddCookie(CookieScheme, options =>
        {
            options.Cookie.Name = PoPlatform.SessionCookieName(secure: true);
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            // Kiosk-durable session: an always-on wall display should survive a full unattended
            // day and slide forward on each authenticated poll, so it never blanks to /login
            // mid-session. Still HttpOnly + SameSite=Strict + Secure — no guardrail relaxed.
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
            options.SlidingExpiration = true;
            // BFF proxies XHR: answer with status codes rather than HTML redirects.
            options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });

        if (oidcConfigured)
        {
            var allowedTenants = azureAd.GetSection("AllowedTenants").Get<string[]>() ?? [];
            var instance = (azureAd["Instance"] ?? "https://login.microsoftonline.com/").TrimEnd('/');
            var tenant = azureAd["TenantId"] ?? "common"; // /common accepts work/school + personal accounts
            if (allowedTenants.Length == 0 && builder.Environment.IsProduction())
                Serilog.Log.Warning("AzureAd:AllowedTenants is empty: any Microsoft account can sign in and get its own data partition. List the tenant ids that may use this app.");

            auth.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.Authority = $"{instance}/{tenant}/v2.0";
                options.ClientId = clientId;
                options.ClientSecret = azureAd["ClientSecret"];
                options.ResponseType = "code";
                options.UsePkce = true;
                options.CallbackPath = azureAd["CallbackPath"] ?? "/signin-oidc";
                options.SignedOutCallbackPath = azureAd["SignedOutCallbackPath"] ?? "/signout-callback-oidc";
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.SignInScheme = CookieScheme;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");

                // Rule 4.3: shape-based issuer validation against an allowed tenant list.
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                {
                    if (allowedTenants.Length == 0)
                        return issuer; // no restriction configured
                    // The tenant is the issuer's first path segment (https://login.microsoftonline.com/{tid}/v2.0).
                    // Compare it whole: a substring match would also accept a tenant id that merely contains an allowed one.
                    var tenantId = Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && uri.Segments.Length > 1
                        ? uri.Segments[1].Trim('/')
                        : null;
                    if (allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
                        return issuer;
                    throw new SecurityTokenInvalidIssuerException(
                        $"Issuer '{issuer}' is not in the configured AllowedTenants list.");
                };
                // Canonical UserSignedIn record. OnTokenValidated fires exactly once per interactive
                // sign-in — after validation, before the session cookie is issued — so it needs no
                // dedupe, and a rejected token never reaches it.
                options.Events.OnTokenValidated = ctx =>
                {
                    SignInTelemetry.TrackFrom(ctx.HttpContext, ctx.Principal, "PoWatch");
                    return Task.CompletedTask;
                };
            });

            auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = $"{instance}/{tenant}/v2.0";
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudiences = [clientId!, $"api://{clientId}"],
                    ValidateIssuer = true,
                    IssuerValidator = (issuer, _, _) =>
                    {
                        if (allowedTenants.Length == 0)
                            return issuer;
                        var tenantId = Uri.TryCreate(issuer, UriKind.Absolute, out var uri) && uri.Segments.Length > 1
                            ? uri.Segments[1].Trim('/')
                            : null;
                        if (allowedTenants.Contains(tenantId, StringComparer.OrdinalIgnoreCase))
                            return issuer;
                        throw new SecurityTokenInvalidIssuerException(
                            $"Issuer '{issuer}' is not in the configured AllowedTenants list.");
                    }
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                            ctx.Token = token;
                        return Task.CompletedTask;
                    }
                };
            });
        }

        // Dev/Test guest bypass: header-based FakeAuth scheme for automated tests (never in Production).
        if (flags.DeveloperBypassAuth)
        {
            if (builder.Environment.IsProduction())
                throw new InvalidOperationException(
                    "DeveloperBypassAuth (FakeAuth) must never be enabled in Production.");
            auth.AddScheme<AuthenticationSchemeOptions, FakeAuthHandler>(FakeAuthHandler.SchemeName, null);
        }

        // Default authorization policy used by every .RequireAuthorization() call. In Production it
        // accepts the BFF cookie and, when Entra OIDC is configured, Bearer JWT tokens from mobile clients.
        var defaultSchemes = new List<string> { CookieScheme };
        if (oidcConfigured)
            defaultSchemes.Add(JwtBearerDefaults.AuthenticationScheme);
        if (flags.DeveloperBypassAuth)
            defaultSchemes.Add(FakeAuthHandler.SchemeName);

        var defaultPolicy = new AuthorizationPolicyBuilder([.. defaultSchemes])
            .RequireAuthenticatedUser()
            .Build();

        // Rule 4.5 default-deny: fallback accepts the BFF cookie and Bearer JWT (when Entra is configured);
        // it must NOT include the always-succeeds FakeAuth scheme.
        var fallbackSchemes = oidcConfigured
            ? new[] { CookieScheme, JwtBearerDefaults.AuthenticationScheme }
            : new[] { CookieScheme };
        var fallbackPolicy = new AuthorizationPolicyBuilder(fallbackSchemes)
            .RequireAuthenticatedUser()
            .Build();

        builder.Services.AddAuthorizationBuilder()
            .SetDefaultPolicy(defaultPolicy)
            .SetFallbackPolicy(fallbackPolicy);

        return oidcConfigured;
    }
}

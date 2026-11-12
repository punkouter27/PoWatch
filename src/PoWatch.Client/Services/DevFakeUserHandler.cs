using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

namespace PoWatch.Client.Services;

public sealed class DevFakeUserHandler(IWebAssemblyHostEnvironment env, IJSRuntime js) : DelegatingHandler
{
    public const string FakeUserStorageKey = "powatch:fake-user";
    public const string FakeRolesStorageKey = "powatch:fake-roles";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!env.IsProduction())
        {
            try
            {
                string? fakeUser = await js.TryInvokeAsync<string?>("localStorage.getItem", FakeUserStorageKey);
                if (!string.IsNullOrWhiteSpace(fakeUser))
                {
                    request.Headers.TryAddWithoutValidation("X-Fake-User", fakeUser);
                    string? fakeRoles = await js.TryInvokeAsync<string?>("localStorage.getItem", FakeRolesStorageKey);
                    if (!string.IsNullOrWhiteSpace(fakeRoles))
                        request.Headers.TryAddWithoutValidation("X-Fake-Roles", fakeRoles);
                }
            }
            catch
            {
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using PoWatch.Application.Options;

namespace PoWatch.Api.Security;

/// <summary>
/// Kiosk-durable session keys. The BFF auth cookie (<c>PoWatch.Auth</c>) is encrypted with
/// ASP.NET Core Data Protection. By default the keyring is generated per-instance and held only in
/// memory, so every App Service recycle, deploy, or scale-out event rotates the keys and silently
/// invalidates every existing cookie — the always-on wall display drops to /login mid-session.
///
/// This wires a <b>durable, shared</b> keyring plus a stable application name so keys survive restarts
/// and are shared across scaled-out instances:
/// <list type="bullet">
///   <item>Hosted environments with Azure Storage → persist the keyring to a blob (managed identity
///   when a ServiceUri is configured, else the storage connection string).</item>
///   <item>Local/dev/test → a stable filesystem folder under the content root, so sign-in works with
///   no Azurite dependency and still survives a process restart.</item>
/// </list>
/// Keys are protected at rest by storage RBAC. Envelope-encrypting them with a Key Vault key
/// (<c>ProtectKeysWithAzureKeyVault</c>) is a reasonable future hardening step.
/// </summary>
public static class DataProtectionSetup
{
    private const string KeysBlobName = "keys.xml";
    private const string ApplicationName = "PoWatch";

    public static void AddPoWatchDataProtection(this WebApplicationBuilder builder)
    {
        var storage = builder.Configuration.GetSection("AzureStorage").Get<AzureStorageOptions>()
                      ?? new AzureStorageOptions();

        var dp = builder.Services.AddDataProtection()
            // Keys are only shared/decryptable across instances that agree on the application name.
            .SetApplicationName(ApplicationName);

        var container = storage.DataProtectionKeysContainer;
        var storageBacked = builder.Environment.IsProduction();

        if (storageBacked
            && !string.IsNullOrWhiteSpace(storage.ServiceUri)
            && Uri.TryCreate(storage.ServiceUri, UriKind.Absolute, out var tableUri))
        {
            // Managed identity: derive the blob endpoint from the table URI host (same account).
            var blobUri = new Uri($"https://{tableUri.Host.Replace(".table.", ".blob.")}/{container}/{KeysBlobName}");
            dp.PersistKeysToAzureBlobStorage(blobUri, new DefaultAzureCredential());
        }
        else if (storageBacked && !string.IsNullOrWhiteSpace(storage.ConnectionString))
        {
            dp.PersistKeysToAzureBlobStorage(storage.ConnectionString, container, KeysBlobName);
        }
        else
        {
            // Local/dev/test (or storage intentionally skipped): a stable on-disk keyring — survives
            // restarts on a single box and needs no external dependency for cookie encryption to work.
            var keysDir = Path.Combine(builder.Environment.ContentRootPath, ".dataprotection-keys");
            Directory.CreateDirectory(keysDir);
            dp.PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        }
    }
}

using System.ComponentModel.DataAnnotations;

namespace PoWatch.Application.Options;

public sealed class AzureStorageOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ServiceUri { get; set; } = string.Empty;

    // Azure table/container names must be 3 to 63 chars. These carry defaults, so
    // validation only trips when an operator overrides them to something invalid — fail-fast at boot.
    [Required, StringLength(63, MinimumLength = 3)]
    public string SessionsTable { get; set; } = "PoWatchSessions";

    [Required, StringLength(63, MinimumLength = 3)]
    public string TicksTable { get; set; } = "PoWatchTicks";

    [Required, StringLength(63, MinimumLength = 3)]
    public string SceneEventsTable { get; set; } = "PoWatchSceneEvents";

    [Required, StringLength(63, MinimumLength = 3)]
    public string IngestLedgerTable { get; set; } = "PoWatchIngestLedger";

    [Required, StringLength(63, MinimumLength = 3)]
    public string RollupsTable { get; set; } = "PoWatchRollups";

    [Required, StringLength(63, MinimumLength = 3)]
    public string RegularsTable { get; set; } = "PoWatchRegulars";

    /// <summary>Retention, weather place and weekly-email choices, one row per user.</summary>
    [Required, StringLength(63, MinimumLength = 3)]
    public string ProfilesTable { get; set; } = "PoWatchProfiles";

    /// <summary>Shared recap links, keyed by token.</summary>
    [Required, StringLength(63, MinimumLength = 3)]
    public string SharesTable { get; set; } = "PoWatchShares";

    /// <summary>Each session's ten-minute chapters and its finished story.</summary>
    [Required, StringLength(63, MinimumLength = 3)]
    public string StoriesTable { get; set; } = "PoWatchStories";

    /// <summary>
    /// Every table the app keeps: created at startup, and purged when a user deletes their data (shares
    /// are keyed by token, so the purge also deletes them by owner).
    /// </summary>
    public string[] AllTables() => [SessionsTable, TicksTable, SceneEventsTable, IngestLedgerTable, RollupsTable, RegularsTable, ProfilesTable, SharesTable, StoriesTable];

    /// <summary>Highlight snapshots, at {user}/{yyyyMMdd}/{id}.jpg.</summary>
    [Required, StringLength(63, MinimumLength = 3)]
    public string SnapshotsContainer { get; set; } = "snapshots";

    // Blob container holding the persisted Data Protection keyring (BFF cookie encryption keys).
    [Required, StringLength(63, MinimumLength = 3)]
    public string DataProtectionKeysContainer { get; set; } = "dataprotection-keys";

    public string[] DevCorsAllowedOrigins { get; set; } = [];
}

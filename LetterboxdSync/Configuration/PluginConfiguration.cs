using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using LetterboxdSync.Security;
using MediaBrowser.Model.Plugins;

namespace LetterboxdSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public List<Account> Accounts { get; set; } = new List<Account>();

    /// <summary>
    /// Serializd (TV) account links, one or more per Jellyfin user. Independent of
    /// <see cref="Accounts"/> (Letterboxd/film); a user can link either, both, or neither.
    /// </summary>
    public List<SerializdAccount> SerializdAccounts { get; set; } = new List<SerializdAccount>();

    /// <summary>
    /// Base URL of the Seerr instance, e.g. "http://192.168.1.122:5055" or "https://requests.example.com".
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Seerr API key (Settings → General → API Key in Seerr).
    /// </summary>
    [XmlIgnore]
    public string? JellyseerrApiKey { get; set; }

    /// <summary>Encrypted on-disk form of <see cref="JellyseerrApiKey"/>. See <see cref="Configuration.Account.LetterboxdPasswordProtected"/> for why this is JsonIgnore'd.</summary>
    [XmlElement("JellyseerrApiKey")]
    [JsonIgnore]
    public string? JellyseerrApiKeyProtected
    {
        get => SecretProtector.Protect(JellyseerrApiKey);
        set => JellyseerrApiKey = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// Anonymous opt-in usage telemetry state. Off by default; nothing is ever sent
    /// while disabled. See <see cref="TelemetryData"/> for what persists and why.
    /// </summary>
    public TelemetryData Telemetry { get; set; } = new();

    /// <summary>
    /// One-shot guard for the catalog migration that adds the proxied (edge-cached)
    /// plugin repository entry alongside the GitHub one (v1.19.0). Set after the
    /// first attempt so a user who deletes the added entry is never overridden.
    /// </summary>
    public bool CatalogMigrationDone { get; set; }

    /// <summary>
    /// Master switch for the Jellyfin Enhanced review sync. Off by default: enabling it posts
    /// real reviews/ratings to real Letterboxd and Serializd accounts, which is not something
    /// to start doing on upgrade. See <see cref="EnhancedReviewSyncTask"/>.
    /// </summary>
    public bool EnhancedReviewSyncEnabled { get; set; }

    /// <summary>
    /// When true (default) the first run posts every JE review already on the server, dated to
    /// when each was written. When false, only reviews written after the integration was first
    /// run are posted — the timestamp is captured once and persisted, so flipping this back and
    /// forth never re-posts history.
    /// </summary>
    public bool EnhancedReviewSyncBackfill { get; set; } = true;

    /// <summary>
    /// Optional explicit path to Jellyfin Enhanced's <c>reviews.json</c>. Leave empty to use
    /// JE's standard location under the sibling plugin configurations directory.
    /// </summary>
    public string? EnhancedReviewsPath { get; set; }

    /// <summary>
    /// How many times a failing review is retried before it is left alone. Applies only to
    /// failures — an entry that was posted, or skipped for a structural reason, is never
    /// retried unless its content or rating changes.
    /// </summary>
    public int EnhancedReviewSyncMaxAttempts { get; set; } = 5;
}

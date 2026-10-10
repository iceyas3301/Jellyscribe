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
    /// Kill switch for <see cref="SidebarScriptStartupFilter"/>, the request-time injection that
    /// adds the sidebar link without the File Transformation plugin. No dashboard control: set it
    /// in the plugin's XML config if the injection ever conflicts with another plugin.
    /// </summary>
    public bool DisableSidebarScriptMiddleware { get; set; }

    /// <summary>
    /// Serializd (TV) account links, one or more per Jellyfin user. Independent of
    /// <see cref="Accounts"/> (Letterboxd/film); a user can link either, both, or neither.
    /// </summary>
    public List<SerializdAccount> SerializdAccounts { get; set; } = new List<SerializdAccount>();

    /// <summary>
    /// Base URL of the Seerr instance, e.g. "http://seerr.local:5055" or "https://requests.example.com".
    /// Trailing slash is stripped at use time.
    /// </summary>
    public string? JellyseerrUrl { get; set; }

    /// <summary>
    /// Seerr API key (Settings → General → API Key in Seerr).
    /// </summary>
    [XmlIgnore]
    [JsonIgnore]
    public string? JellyseerrApiKey { get; set; }

    /// <summary>Write-only JSON form of <see cref="JellyseerrApiKey"/>. See <see cref="Account.LetterboxdPasswordInput"/>.</summary>
    [XmlIgnore]
    [JsonPropertyName("JellyseerrApiKey")]
    public string? JellyseerrApiKeyInput
    {
        internal get => JellyseerrApiKey;
        set => JellyseerrApiKey = value;
    }

    [XmlIgnore]
    public bool HasJellyseerrApiKey => !string.IsNullOrEmpty(JellyseerrApiKey);

    /// <summary>Write-only: true on a config POST drops the stored key, which an empty value would keep.</summary>
    [XmlIgnore]
    public bool ClearJellyseerrApiKey { internal get; set; }

    /// <summary>Encrypted on-disk form of <see cref="JellyseerrApiKey"/>. See <see cref="Configuration.Account.LetterboxdPasswordProtected"/> for why this is JsonIgnore'd.</summary>
    [XmlElement("JellyseerrApiKey")]
    [JsonIgnore]
    public string? JellyseerrApiKeyProtected
    {
        get => SecretProtector.Protect(JellyseerrApiKey);
        set => JellyseerrApiKey = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// Approve the requests this plugin creates in Seerr, so they actually reach Radarr/Sonarr.
    /// <para>
    /// The plugin creates each request as the Seerr user it is for (X-API-User), so Seerr applies
    /// that user's own permissions: a user without "Auto-Approve" gets a PENDING request, which
    /// Seerr never hands to Radarr/Sonarr until someone approves it (the symptom of issue #110).
    /// With this on, the plugin follows a PENDING request with POST /api/v1/request/{id}/approve
    /// using the admin API key; the Letterboxd watchlist sync also approves that user's earlier
    /// pending requests for films on the watchlist (SeerrClient.ApprovePendingForUserAsync).
    /// </para>
    /// <para>
    /// Defaults to true: enabling per-account auto-request already expresses "go and fetch these".
    /// Turn it off and requests wait in Seerr's approval queue unless the requesting Seerr user
    /// has Auto-Approve there.
    /// </para>
    /// </summary>
    public bool AutoApproveJellyseerrRequests { get; set; } = true;

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

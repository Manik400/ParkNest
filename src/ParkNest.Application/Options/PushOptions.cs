namespace ParkNest.Application.Options;

/// <summary>
/// Firebase Cloud Messaging, or nothing.
///
/// Unlike SMS and payments there is no Production guard here. A deployment with push switched off
/// still delivers every message — the stored notification is the durable record and the socket
/// carries the live one — so refusing to boot over a missing Firebase project would take the whole
/// platform down to protect a convenience.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>"Firebase", or "None" to keep the stored notification and skip the push.</summary>
    public string Provider { get; set; } = "None";

    /// <summary>The Firebase project id. Part of the v1 send URL, so it cannot be inferred.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Path to the service account JSON Google issues for the project. A file path rather than the
    /// JSON inline because it carries a private key, and a private key pasted into configuration
    /// ends up in a repository sooner or later.
    /// </summary>
    public string ServiceAccountKeyPath { get; set; } = string.Empty;

    public bool IsConfigured =>
        string.Equals(Provider, "Firebase", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ProjectId)
        && !string.IsNullOrWhiteSpace(ServiceAccountKeyPath);
}

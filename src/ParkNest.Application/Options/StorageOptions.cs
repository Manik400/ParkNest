namespace ParkNest.Application.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>"Local" writes to a directory the API also serves; "None" disables photo upload.</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>
    /// Where the files go. Relative paths resolve against the content root, so the default keeps a
    /// development machine tidy without any configuration.
    /// </summary>
    public string LocalRoot { get; set; } = "media";

    /// <summary>The path prefix the files are served under. Must match the static-file mapping.</summary>
    public string PublicPath { get; set; } = "/media";

    /// <summary>
    /// Ceiling on one upload. Phone cameras produce several megabytes per shot and a host has no
    /// reason to send the original — this is generous for a photo of a driveway and cheap to refuse.
    /// </summary>
    public long MaxPhotoBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    /// Photos one listing may hold. A cap so a single host cannot fill the disk, and because past
    /// half a dozen shots nobody looks anyway.
    /// </summary>
    public int MaxPhotosPerListing { get; set; } = 6;

    public bool IsDisabled =>
        string.IsNullOrWhiteSpace(Provider) || string.Equals(Provider, "None", StringComparison.OrdinalIgnoreCase);

    public bool IsLocal => string.Equals(Provider, "Local", StringComparison.OrdinalIgnoreCase);
}

namespace ParkNest.Application.Options;

/// <summary>
/// Identity verification, which is what stands between a host's earnings and their bank account.
/// </summary>
public sealed class KycOptions
{
    public const string SectionName = "Kyc";

    /// <summary>
    /// Secret mixed into the hash of a document number.
    ///
    /// Without it the hash is worthless: PAN and Aadhaar have small, structured keyspaces, and an
    /// unsalted digest of one is reversible by anybody with a wordlist and an afternoon. Keyed, the
    /// hash still answers "have I seen this document before" and answers nothing else.
    /// </summary>
    public string Pepper { get; set; } = string.Empty;

    /// <summary>
    /// Whether a photograph of the document is required.
    ///
    /// On by default, because a number typed into a form proves only that someone knows a number.
    /// Turned off where photo storage is not configured — the alternative is a KYC flow that
    /// cannot be completed at all on a deployment that never wired up a bucket.
    /// </summary>
    public bool RequireDocumentPhoto { get; set; } = true;
}

namespace ParkNest.Application.Options;

public sealed class FeedbackOptions
{
    public const string SectionName = "Feedback";

    /// <summary>
    /// The inbox that problem reports and feedback are mailed to. Left empty, they go to the first
    /// of <c>Auth:AdminEmails</c> — the platform owner — so a deployment is never without one.
    /// </summary>
    public string ToAddress { get; set; } = string.Empty;
}

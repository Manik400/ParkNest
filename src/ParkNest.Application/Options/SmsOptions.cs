namespace ParkNest.Application.Options;

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary>"Msg91", or "Log" to print codes to the console in development.</summary>
    public string Provider { get; set; } = "Log";

    /// <summary>MSG91 auth key. Must come from a secret store.</summary>
    public string AuthKey { get; set; } = string.Empty;

    /// <summary>Approved DLT template id. Indian carriers reject unregistered templates outright.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Registered sender id, e.g. "PRKNST".</summary>
    public string SenderId { get; set; } = "PRKNST";

    public bool IsConfigured =>
        string.Equals(Provider, "Msg91", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(AuthKey)
        && !string.IsNullOrWhiteSpace(TemplateId);
}

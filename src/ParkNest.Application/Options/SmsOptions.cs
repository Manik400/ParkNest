namespace ParkNest.Application.Options;

public sealed class SmsOptions
{
    public const string SectionName = "Sms";

    /// <summary>
    /// "Msg91", "Gateway" for a self-hosted SMS gateway, or "Log" to print codes to the console
    /// in development.
    /// </summary>
    public string Provider { get; set; } = "Log";

    /// <summary>MSG91 auth key. Must come from a secret store.</summary>
    public string AuthKey { get; set; } = string.Empty;

    /// <summary>Approved DLT template id. Indian carriers reject unregistered templates outright.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Registered sender id, e.g. "PRKNST".</summary>
    public string SenderId { get; set; } = "PRKNST";

    /// <summary>
    /// Root of the self-hosted gateway, e.g. <c>http://192.168.1.20:8080</c> for SMS Gateway for
    /// Android on the local network, or <c>https://api.sms-gate.app/3rdparty/v1</c> for its cloud
    /// relay. Used only by <c>Provider=Gateway</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Basic-auth user for the gateway.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Basic-auth password for the gateway. Must come from a secret store.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Body of the message, with <c>{code}</c> substituted. Kept configurable because the wording
    /// is what a DLT template is registered against, and an Indian carrier drops transactional SMS
    /// whose text does not match the registered template to the character.
    /// </summary>
    public string MessageTemplate { get; set; } = "{code} is your ParkNest verification code. It expires in 5 minutes. Do not share it with anyone.";

    /// <summary>
    /// Default country code prefixed to a bare ten-digit number before it reaches a gateway that
    /// wants E.164. India, matching the rest of the platform.
    /// </summary>
    public string DefaultCountryCode { get; set; } = "91";

    private bool IsMsg91 =>
        string.Equals(Provider, "Msg91", StringComparison.OrdinalIgnoreCase);

    /// <summary>A self-hosted gateway: an Android handset with a SIM, or a GSM modem.</summary>
    public bool IsGateway =>
        string.Equals(Provider, "Gateway", StringComparison.OrdinalIgnoreCase);

    public bool IsMsg91Configured =>
        IsMsg91
        && !string.IsNullOrWhiteSpace(AuthKey)
        && !string.IsNullOrWhiteSpace(TemplateId);

    public bool IsGatewayConfigured =>
        IsGateway
        && !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password);

    /// <summary>Whether some sender that actually reaches a handset is configured.</summary>
    public bool IsConfigured => IsMsg91Configured || IsGatewayConfigured;
}

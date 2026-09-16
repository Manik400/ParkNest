namespace ParkNest.Application.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// "Smtp" to send through any SMTP server, "Brevo" to send over Brevo's HTTPS API (for hosts
    /// that block SMTP ports), or "Log" to print codes to the console in development.
    /// </summary>
    public string Provider { get; set; } = "Log";

    /// <summary>Brevo API key (starts with xkeysib-). Must come from a secret store.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>e.g. smtp.gmail.com, smtp-relay.brevo.com, or localhost for a mail catcher.</summary>
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary>
    /// "StartTls" for port 587, "SslOnConnect" for 465, "None" only for a local mail catcher. There
    /// is deliberately no opportunistic mode: a network that stripped the STARTTLS offer would
    /// otherwise be handed the password in the clear.
    /// </summary>
    public string Security { get; set; } = "StartTls";

    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// For Gmail, a 16-character App Password — never the account password. Must come from a
    /// secret store.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Defaults to <see cref="Username"/>, which is what Gmail requires anyway.</summary>
    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "ParkNest";

    public bool IsSmtp =>
        string.Equals(Provider, "Smtp", StringComparison.OrdinalIgnoreCase);

    public string SenderAddress =>
        string.IsNullOrWhiteSpace(FromAddress) ? Username : FromAddress;

    public bool HasKnownSecurity =>
        Security.Equals("StartTls", StringComparison.OrdinalIgnoreCase)
        || Security.Equals("SslOnConnect", StringComparison.OrdinalIgnoreCase)
        || Security.Equals("None", StringComparison.OrdinalIgnoreCase);

    /// <summary>A user without a password is a typo; no user at all is a mail catcher.</summary>
    public bool IsSmtpConfigured =>
        IsSmtp
        && !string.IsNullOrWhiteSpace(Host)
        && !string.IsNullOrWhiteSpace(SenderAddress)
        && HasKnownSecurity
        && (string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password));

    public bool IsBrevo =>
        string.Equals(Provider, "Brevo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Brevo only sends from an address verified in its dashboard, so one is required.</summary>
    public bool IsBrevoConfigured =>
        IsBrevo
        && !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(SenderAddress);

    public bool IsConfigured => IsSmtpConfigured || IsBrevoConfigured;
}

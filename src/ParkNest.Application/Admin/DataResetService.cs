using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;

namespace ParkNest.Application.Admin;

/// <summary>What the reset removed, table by table, so the operator sees what they just did.</summary>
public sealed record DataResetResult(IReadOnlyDictionary<string, long> RowsRemoved, IReadOnlyList<string> Kept);

/// <summary>
/// Removes everything but the accounts and the price bands. Provider-specific, so it lives in the
/// infrastructure layer; this interface is what the application asks for.
/// </summary>
public interface IDataWiper
{
    Task<DataResetResult> WipeAsync(CancellationToken cancellationToken = default);
}

public interface IDataResetService
{
    /// <summary>The phrase the operator has to type. Shown on the form, checked here.</summary>
    string ConfirmationPhrase { get; }

    bool IsAllowed { get; }

    Task<DataResetResult> ResetAsync(string confirmation, CancellationToken cancellationToken = default);
}

/// <summary>
/// The guard around the wipe: the admin role, the configuration switch, and a phrase typed by hand.
///
/// Three checks for one button, because the button deletes every booking, wallet and ledger row
/// on the platform. The role says who; the switch says whether this deployment is one where that
/// is ever acceptable; the phrase says they meant it, on this click, rather than on the one
/// next to it.
/// </summary>
public sealed class DataResetService : IDataResetService
{
    private readonly IDataWiper _wiper;
    private readonly ICurrentUser _currentUser;
    private readonly MaintenanceOptions _options;
    private readonly ILogger<DataResetService> _logger;

    public DataResetService(
        IDataWiper wiper,
        ICurrentUser currentUser,
        IOptions<MaintenanceOptions> options,
        ILogger<DataResetService> logger)
    {
        _wiper = wiper;
        _currentUser = currentUser;
        _options = options.Value;
        _logger = logger;
    }

    public string ConfirmationPhrase => "RESET ALL DATA";

    public bool IsAllowed => _options.AllowDataReset;

    public async Task<DataResetResult> ResetAsync(string confirmation, CancellationToken cancellationToken = default)
    {
        _currentUser.RequireAdmin();

        if (!_options.AllowDataReset)
        {
            throw new ForbiddenException("Data reset is switched off in this environment (Maintenance:AllowDataReset).");
        }

        if (!string.Equals(confirmation?.Trim(), ConfirmationPhrase, StringComparison.Ordinal))
        {
            throw new DomainException($"Type {ConfirmationPhrase} exactly to confirm.");
        }

        var userId = _currentUser.RequireUserId();
        _logger.LogWarning("Data reset requested by admin {UserId}.", userId);

        var result = await _wiper.WipeAsync(cancellationToken);

        _logger.LogWarning(
            "Data reset complete: {Rows} rows removed across {Tables} tables.",
            result.RowsRemoved.Values.Sum(), result.RowsRemoved.Count);

        return result;
    }
}

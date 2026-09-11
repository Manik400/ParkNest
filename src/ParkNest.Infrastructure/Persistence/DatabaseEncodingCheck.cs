using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ParkNest.Infrastructure.Persistence;

/// <summary>
/// Says loudly, at startup, if the database cannot store the text this application handles.
///
/// This exists because it happened. A database created from a Windows default landed on WIN1252,
/// which has no rupee sign — and no Devanagari, Kannada or Tamil either. The first symptom was a
/// notification failing to save; the actual problem was a database that could not hold a large
/// share of its users' names.
///
/// Nothing here can fix it: encoding is fixed when the database is created, and changing it means
/// a dump, a recreate and a restore. So the check reports rather than repairs, and does it at
/// startup where somebody is watching, instead of at the first insert containing a non-Latin
/// character — which might be a week after launch.
/// </summary>
public sealed class DatabaseEncodingCheck : IHostedService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DatabaseEncodingCheck> _logger;

    public DatabaseEncodingCheck(IServiceScopeFactory scopes, ILogger<DatabaseEncodingCheck> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ParkNestDbContext>();

            if (db.Database.ProviderName?.Contains("Npgsql") != true)
            {
                return;
            }

            var encoding = await db.Database
                .SqlQuery<string>(
                    $"SELECT pg_encoding_to_char(encoding) AS \"Value\" FROM pg_database WHERE datname = current_database()")
                .FirstOrDefaultAsync(cancellationToken);

            if (string.Equals(encoding, "UTF8", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.LogError(
                "The database is encoded as {Encoding}, not UTF8. It cannot store Indian-language "
                + "names, addresses, or a rupee sign, and writes containing them will fail. "
                + "Encoding is fixed at creation: recreate with ENCODING 'UTF8' TEMPLATE template0 "
                + "and restore.",
                encoding);
        }
        catch (Exception ex)
        {
            // A check that cannot run must not stop the API. The database being briefly
            // unreachable at startup is a different problem with its own, louder symptoms.
            _logger.LogWarning(ex, "Could not read the database encoding.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

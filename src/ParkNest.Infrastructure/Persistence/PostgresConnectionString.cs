using Npgsql;

namespace ParkNest.Infrastructure.Persistence;

/// <summary>
/// Accepts a Postgres connection string in either shape a person is likely to paste.
///
/// Npgsql reads only the keyword form (<c>Host=…;Database=…</c>). Hosted Postgres providers —
/// Neon, Aiven, Supabase, Render — hand out the URL form first
/// (<c>postgresql://user:password@host/db?sslmode=require</c>), and pasting that into
/// <c>ConnectionStrings__ParkNest</c> used to kill the process at the first query with
/// "Couldn't set postgresql://…". Converting here, once, means the value from the provider's
/// dashboard works as copied.
/// </summary>
public static class PostgresConnectionString
{
    public static string Normalise(string connectionString)
    {
        var value = connectionString.Trim();

        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:ParkNest looks like a postgresql:// URL but could not be read. " +
                "Check it was copied whole, or use the Host=…;Database=…;Username=…;Password=… form.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
        };

        // user:password, each percent-encoded in a URL — a password with @ or : in it arrives
        // escaped, and would sign in as the wrong thing if used raw.
        var userInfo = uri.UserInfo.Split(':', 2);

        if (userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }

        if (userInfo.Length == 2)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]).ToLowerInvariant();
            var setting = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            switch (key)
            {
                case "sslmode":
                    builder.SslMode = setting.ToLowerInvariant() switch
                    {
                        "disable" => SslMode.Disable,
                        "allow" => SslMode.Allow,
                        "prefer" => SslMode.Prefer,
                        "verify-ca" => SslMode.VerifyCA,
                        "verify-full" => SslMode.VerifyFull,
                        _ => SslMode.Require
                    };
                    break;

                case "channel_binding":
                    builder.ChannelBinding = setting.ToLowerInvariant() switch
                    {
                        "disable" => ChannelBinding.Disable,
                        "require" => ChannelBinding.Require,
                        _ => ChannelBinding.Prefer
                    };
                    break;

                case "application_name":
                    builder.ApplicationName = setting;
                    break;

                // libpq options Npgsql has no equivalent for, or that only tune the client, are
                // dropped rather than failing the whole string over one unknown flag.
            }
        }

        return builder.ConnectionString;
    }
}

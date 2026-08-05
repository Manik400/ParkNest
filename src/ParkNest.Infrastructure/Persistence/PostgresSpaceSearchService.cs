using Microsoft.EntityFrameworkCore;
using Npgsql;
using ParkNest.Application.Listings;
using ParkNest.Domain.Common;

namespace ParkNest.Infrastructure.Persistence;

/// <summary>
/// Geo-search runs as parameterised raw SQL against the PostGIS <c>geog</c> column rather than
/// through EF's spatial mapping, so the domain model stays provider-agnostic and testable on
/// SQLite. See docs/adr/0003-geo-search-without-nts.md.
/// </summary>
public sealed class PostgresSpaceSearchService : ISpaceSearchService
{
    private readonly ParkNestDbContext _db;

    public PostgresSpaceSearchService(ParkNestDbContext db) => _db = db;

    public async Task<IReadOnlyList<NearbySpace>> SearchNearbyAsync(
        NearbySearchQuery query,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  s."Id",
                    s."Title",
                    s."AddressLine",
                    s."Latitude",
                    s."Longitude",
                    s."PricePerHour",
                    ST_Distance(s.geog, @origin) AS distance_metres
            FROM    parking_spaces s
            WHERE   s."Status" = 1
              AND   ST_DWithin(s.geog, @origin, @radius)
              AND   (@vehicle_type IS NULL OR EXISTS (
                        SELECT 1 FROM space_vehicle_supports v
                        WHERE v."ParkingSpaceId" = s."Id" AND v."VehicleType" = @vehicle_type))
              AND   (@max_price IS NULL OR s."PricePerHour" <= @max_price)
            ORDER BY distance_metres
            LIMIT   @limit;
            """;

        var origin = $"SRID=4326;POINT({query.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                     $"{query.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)})";

        await using var connection = new NpgsqlConnection(_db.Database.GetConnectionString());
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);

        // Every parameter is explicitly typed. The optional filters are compared against NULL in
        // the SQL, and Postgres cannot infer a type for an untyped NULL — it fails the whole
        // statement with "could not determine data type of parameter".
        command.Parameters.Add(new NpgsqlParameter("origin", NpgsqlTypes.NpgsqlDbType.Unknown) { Value = origin });
        command.Parameters.Add(new NpgsqlParameter("radius", NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.RadiusMetres });
        command.Parameters.Add(new NpgsqlParameter("vehicle_type", NpgsqlTypes.NpgsqlDbType.Integer)
        {
            Value = query.VehicleType is null ? DBNull.Value : (int)query.VehicleType.Value
        });
        command.Parameters.Add(new NpgsqlParameter("max_price", NpgsqlTypes.NpgsqlDbType.Numeric)
        {
            Value = query.MaxPricePerHour ?? (object)DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.Limit });

        var results = new List<NearbySpace>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new NearbySpace(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDecimal(5),
                reader.GetDouble(6)));
        }

        return results;
    }
}

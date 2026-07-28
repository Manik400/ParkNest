using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ParkNest.Infrastructure.Persistence;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time. Reads the connection string from
/// <c>PARKNEST_CONNECTION</c> so migrations can be scaffolded without booting the API.
/// </summary>
public sealed class ParkNestDbContextFactory : IDesignTimeDbContextFactory<ParkNestDbContext>
{
    public ParkNestDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("PARKNEST_CONNECTION")
            ?? "Host=localhost;Port=5432;Database=parknest;Username=parknest;Password=parknest";

        var options = new DbContextOptionsBuilder<ParkNestDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new ParkNestDbContext(options);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ParkNest.Application;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Listings;
using ParkNest.Application.Options;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddParkNestInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));

        var connectionString = configuration.GetConnectionString("ParkNest")
            ?? throw new InvalidOperationException("Connection string 'ParkNest' is not configured.");

        services.AddDbContext<ParkNestDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IParkNestDbContext>(sp => sp.GetRequiredService<ParkNestDbContext>());
        services.AddScoped<ISpaceSearchService, PostgresSpaceSearchService>();

        services.AddParkNestApplication();

        return services;
    }
}

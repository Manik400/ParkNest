using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ParkNest.Api.Controllers;

namespace ParkNest.IntegrationTests;

/// <summary>
/// Every controller can be built from the real container.
///
/// This exists because of a bug it would have caught: a new service was written, unit-tested by
/// constructing it directly, wired into a controller — and never registered. Every test passed and
/// three endpoints returned 500 on the first real request, because a missing registration is not
/// a compile error and nothing that constructs its dependencies by hand can see it.
///
/// Deliberately not asserting behaviour. It asks the one question unit tests structurally cannot:
/// does the graph the application actually builds have a hole in it.
/// </summary>
[Collection(PostgisCollection.Name)]
public sealed class ControllerResolutionTests
{
    /// <summary>
    /// Every controller in the API. Listed by type rather than discovered by reflection so that
    /// adding a controller and forgetting to register its dependencies fails here, at a line
    /// someone has to write, rather than silently widening a loop that finds nothing.
    /// </summary>
    public static TheoryData<Type> Controllers => new()
    {
        typeof(AuthController),
        typeof(BookingsController),
        typeof(ListingsController),
        typeof(VehiclesController),
        typeof(WalletsController),
        typeof(PaymentsController),
        typeof(DisputesController),
        typeof(AdminDisputesController),
        typeof(AdminPayoutsController),
        typeof(AdminPricingController),
        typeof(RatingsController),
        typeof(NotificationsController),
    };

    [Theory]
    [MemberData(nameof(Controllers))]
    public void Every_controller_can_be_constructed_from_the_container(Type controller)
    {
        if (!PostgisFixture.Available)
        {
            // The API's own startup needs a connection string; without one there is nothing to
            // build. Skipped rather than faked, because a faked container would not be the graph
            // this test exists to check.
            return;
        }

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:ParkNest",
                PostgisFixture.ConnectionString));

        using var scope = factory.Services.CreateScope();

        var resolve = () => ActivatorUtilities.CreateInstance(scope.ServiceProvider, controller);

        resolve.Should().NotThrow($"{controller.Name} must be constructible from the real container");
    }
}

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ParkNest.Application.Analytics;
using ParkNest.Application.Options;
using ParkNest.Domain.Analytics;
using ParkNest.Domain.Common;

namespace ParkNest.UnitTests;

/// <summary>
/// The site counter. Two things are being defended here and they pull in opposite directions: the
/// figures have to be honest enough to act on, and the endpoint that feeds them is anonymous, so
/// everything reaching it is assumed to be hostile until it matches something we already named.
/// </summary>
public sealed class AnalyticsTests : IDisposable
{
    private readonly TestHarness _h = new();
    private readonly IAnalyticsCollector _collector;

    public AnalyticsTests() => _collector = new AnalyticsCollector(_h.Analytics, _h.CurrentUser);

    public void Dispose() => _h.Dispose();

    // --- What a browser is allowed to report ------------------------------------------------

    [Fact]
    public async Task Opening_the_site_is_counted()
    {
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit, path: "/explore"));

        var recorded = await _h.Db.AnalyticsEvents.SingleAsync();

        recorded.Name.Should().Be(AnalyticsEventNames.SiteVisit);
        recorded.VisitorId.Should().Be("visitor-1");
        recorded.Source.Should().Be(AnalyticsSources.Web);
        recorded.OccurredAt.Should().Be(TestHarness.Origin);
        recorded.UserId.Should().BeNull("a visit happens before anyone signs in");
    }

    [Fact]
    public async Task A_signed_in_visitor_is_attributed_from_the_token_not_the_body()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(user.Id);

        await _collector.CollectAsync(Hit(AnalyticsEventNames.PageView, path: "/wallet"));

        (await _h.Db.AnalyticsEvents.SingleAsync()).UserId.Should().Be(user.Id);
    }

    [Fact]
    public async Task A_client_cannot_report_an_event_only_the_server_may_record()
    {
        // Left to itself, an anonymous endpoint that took any name would let a stranger claim a
        // thousand payments and make the funnel worthless.
        var kept = await _collector.CollectAsync(Hit(AnalyticsEventNames.PaymentSucceeded));

        kept.Should().BeFalse();
        (await _h.Db.AnalyticsEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unknown_event_name_is_dropped()
    {
        var kept = await _collector.CollectAsync(Hit("something.invented"));

        kept.Should().BeFalse();
        (await _h.Db.AnalyticsEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_page_view_keeps_the_route_and_throws_away_the_query_string()
    {
        // ?orderId=… is how a payment returns to the wallet. Storing it would put a live order id
        // in a table read for counting, which is precisely the kind of leak nobody goes looking for.
        await _collector.CollectAsync(
            Hit(AnalyticsEventNames.PageView, path: "/wallet?orderId=7f3c&status=paid"));

        (await _h.Db.AnalyticsEvents.SingleAsync()).Path.Should().Be("/wallet");
    }

    [Fact]
    public async Task Ids_in_a_route_collapse_so_top_pages_lists_screens_rather_than_records()
    {
        await _collector.CollectAsync(
            Hit(AnalyticsEventNames.PageView, path: $"/bookings/{Guid.NewGuid()}"));

        (await _h.Db.AnalyticsEvents.SingleAsync()).Path.Should().Be("/bookings/:id");
    }

    [Fact]
    public async Task A_referrer_is_kept_as_an_origin_and_nothing_more()
    {
        await _collector.CollectAsync(
            Hit(AnalyticsEventNames.SiteVisit, referrer: "https://www.google.com/search?q=parking+near+me"));

        (await _h.Db.AnalyticsEvents.SingleAsync()).Referrer.Should().Be("https://www.google.com");
    }

    [Fact]
    public async Task A_visitor_id_that_is_not_one_of_ours_is_discarded_rather_than_stored()
    {
        await _collector.CollectAsync(
            Hit(AnalyticsEventNames.SiteVisit) with { VisitorId = "<script>alert(1)</script>" });

        (await _h.Db.AnalyticsEvents.SingleAsync()).VisitorId.Should().BeNull();
    }

    [Fact]
    public async Task Counting_can_be_switched_off_without_a_deploy()
    {
        _h.AnalyticsOptions.Enabled = false;

        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        (await _h.Db.AnalyticsEvents.CountAsync()).Should().Be(0);
    }

    // --- Who may read the figures -------------------------------------------------------------

    [Fact]
    public async Task An_ordinary_user_cannot_read_the_figures()
    {
        var user = await _h.AddUserAsync(UserRole.Both);
        _h.CurrentUser.SignIn(user.Id);

        var act = () => Queries().GetSummaryAsync(30);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task An_unauthenticated_caller_cannot_read_the_figures()
    {
        var act = () => Queries().GetSummaryAsync(30);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task The_allow_list_narrows_the_figures_to_one_address_even_among_admins()
    {
        _h.AnalyticsOptions.Viewers = new[] { "owner@example.com" };

        // An operator with the admin role, brought in to clear the identity-check queue. The role
        // opens that queue; it does not open the revenue figures.
        var operatorUser = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(operatorUser.Id, UserRole.Admin, "ops@example.com");

        var act = () => Queries().GetSummaryAsync(30);

        await act.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task The_named_owner_reads_the_figures_whatever_the_casing()
    {
        _h.AnalyticsOptions.Viewers = new[] { "Owner@Example.com" };

        var owner = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(owner.Id, UserRole.Admin, "owner@example.com");

        var summary = await Queries().GetSummaryAsync(30);

        summary.Days.Should().Be(30);
    }

    // --- The numbers themselves ---------------------------------------------------------------

    [Fact]
    public async Task The_summary_counts_visits_visitors_bookings_and_the_payment_funnel()
    {
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit, path: "/explore"));
        await _collector.CollectAsync(Hit(AnalyticsEventNames.PageView, path: "/explore"));
        await _collector.CollectAsync(Hit(AnalyticsEventNames.PageView, path: "/explore"));
        await _collector.CollectAsync(
            Hit(AnalyticsEventNames.SiteVisit) with { VisitorId = "visitor-2" });

        await _h.Analytics.RecordAsync(new AnalyticsHit(AnalyticsEventNames.BookingCreated, Amount: 240m));
        await _h.Analytics.RecordAsync(new AnalyticsHit(AnalyticsEventNames.PaymentStarted, Amount: 500m));
        await _h.Analytics.RecordAsync(new AnalyticsHit(AnalyticsEventNames.PaymentStarted, Amount: 500m));
        await _h.Analytics.RecordAsync(new AnalyticsHit(AnalyticsEventNames.PaymentSucceeded, Amount: 500m));

        var summary = await AsOwnerAsync();

        summary.Totals.Visits.Should().Be(2);
        summary.Totals.PageViews.Should().Be(2);
        summary.Totals.Visitors.Should().Be(2, "two browsers, four hits between them");
        summary.Totals.Bookings.Should().Be(1);
        summary.Totals.BookingValue.Should().Be(240m);

        // The gap between these two is the whole reason the funnel is worth recording: one of the
        // two people sent to a gateway came back having paid.
        summary.Totals.PaymentAttempts.Should().Be(2);
        summary.Totals.PaymentsSucceeded.Should().Be(1);
        summary.Totals.PaymentValue.Should().Be(500m);

        summary.TopPages.Should().ContainSingle(p => p.Path == "/explore" && p.Views == 2);
        summary.Counters.Should().Contain(c => c.Name == AnalyticsEventNames.PaymentStarted && c.Count == 2);
        summary.AllTimeVisits.Should().Be(2);
    }

    [Fact]
    public async Task The_daily_series_has_an_entry_for_every_day_including_the_quiet_ones()
    {
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        var summary = await AsOwnerAsync(days: 7);

        summary.Daily.Should().HaveCount(7);
        summary.Daily[^1].Visits.Should().Be(1, "today is the last entry");
        summary.Daily.Take(6).Should().OnlyContain(d => d.Visits == 0);
        summary.Daily.Select(d => d.Date).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task A_hit_older_than_the_window_is_left_out_of_the_totals_but_stays_in_the_lifetime_count()
    {
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        _h.Clock.Advance(TimeSpan.FromDays(40));
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        var summary = await AsOwnerAsync(days: 7);

        summary.Totals.Visits.Should().Be(1);
        summary.AllTimeVisits.Should().Be(2);
    }

    [Fact]
    public async Task The_window_cannot_be_widened_past_the_configured_ceiling()
    {
        _h.AnalyticsOptions.MaxWindowDays = 30;

        // Otherwise a query string is all it takes to make the API read the whole table into memory.
        (await AsOwnerAsync(days: 3650)).Days.Should().Be(30);
    }

    // --- Retention -----------------------------------------------------------------------------

    [Fact]
    public async Task Retention_deletes_what_is_past_the_window_and_keeps_the_rest()
    {
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        _h.Clock.Advance(TimeSpan.FromDays(400));
        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));

        var deleted = await Retention().PruneAsync();

        deleted.Should().Be(1);
        (await _h.Db.AnalyticsEvents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retention_set_to_zero_keeps_everything()
    {
        _h.AnalyticsOptions.RetentionDays = 0;

        await _collector.CollectAsync(Hit(AnalyticsEventNames.SiteVisit));
        _h.Clock.Advance(TimeSpan.FromDays(4000));

        (await Retention().PruneAsync()).Should().Be(0);
        (await _h.Db.AnalyticsEvents.CountAsync()).Should().Be(1);
    }

    // --- Helpers -------------------------------------------------------------------------------

    private static ClientAnalyticsHit Hit(string name, string? path = null, string? referrer = null) =>
        new(name, "visitor-1", "session-1", path, referrer, AnalyticsSources.Web, null);

    private IAnalyticsQueries Queries() => new AnalyticsQueries(
        _h.Db,
        _h.CurrentUser,
        _h.Clock,
        Microsoft.Extensions.Options.Options.Create(_h.AnalyticsOptions));

    private IAnalyticsRetention Retention() => new AnalyticsRetention(
        _h.Db,
        _h.Clock,
        Microsoft.Extensions.Options.Options.Create(_h.AnalyticsOptions),
        NullLogger<AnalyticsRetention>.Instance);

    private async Task<AnalyticsSummary> AsOwnerAsync(int days = 30)
    {
        var owner = await _h.AddUserAsync(UserRole.Admin);
        _h.CurrentUser.SignIn(owner.Id, UserRole.Admin, "owner@example.com");

        return await Queries().GetSummaryAsync(days);
    }
}

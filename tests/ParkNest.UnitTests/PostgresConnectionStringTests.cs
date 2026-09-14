using FluentAssertions;
using Npgsql;
using ParkNest.Infrastructure.Persistence;

namespace ParkNest.UnitTests;

/// <summary>
/// Hosted providers hand out postgresql:// URLs; Npgsql reads only Host=…;Database=…. Pasting the
/// URL used to crash the process at the first query, so both shapes are accepted.
/// </summary>
public sealed class PostgresConnectionStringTests
{
    [Fact]
    public void A_neon_url_becomes_the_keyword_form_with_ssl_required()
    {
        var normalised = PostgresConnectionString.Normalise(
            "postgresql://neondb_owner:s3cret@ep-cool-name-123.us-east-2.aws.neon.tech/neondb?sslmode=require&channel_binding=require");

        var built = new NpgsqlConnectionStringBuilder(normalised);
        built.Host.Should().Be("ep-cool-name-123.us-east-2.aws.neon.tech");
        built.Port.Should().Be(5432);
        built.Database.Should().Be("neondb");
        built.Username.Should().Be("neondb_owner");
        built.Password.Should().Be("s3cret");
        built.SslMode.Should().Be(SslMode.Require);
        built.ChannelBinding.Should().Be(ChannelBinding.Require);
    }

    [Fact]
    public void A_port_and_percent_encoded_password_are_honoured()
    {
        var built = new NpgsqlConnectionStringBuilder(PostgresConnectionString.Normalise(
            "postgres://avnadmin:p%40ss%3Aword@pg-abc.aivencloud.com:14329/defaultdb?sslmode=require&connect_timeout=10"));

        built.Port.Should().Be(14329);
        built.Password.Should().Be("p@ss:word");
        built.Database.Should().Be("defaultdb");
    }

    [Fact]
    public void The_keyword_form_passes_through_unchanged()
    {
        const string keyword = "Host=localhost;Port=54321;Database=parknest;Username=parknest;Password=parknest";

        PostgresConnectionString.Normalise(keyword).Should().Be(keyword);
    }

    [Fact]
    public void A_url_with_no_host_is_refused_with_a_readable_message()
    {
        var act = () => PostgresConnectionString.Normalise("postgresql://");

        act.Should().Throw<InvalidOperationException>().WithMessage("*postgresql:// URL*");
    }
}

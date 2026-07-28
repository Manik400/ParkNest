namespace ParkNest.Domain.Common;

/// <summary>
/// Credits are stored as decimal(18,2). Every amount that enters the ledger goes through
/// <see cref="Round"/> first, so a rounding difference can never leave a transaction unbalanced.
/// </summary>
public static class Money
{
    public const int Scale = 2;

    public static decimal Round(decimal value) => Math.Round(value, Scale, MidpointRounding.AwayFromZero);

    public static bool IsPositive(decimal value) => value > 0m;
}

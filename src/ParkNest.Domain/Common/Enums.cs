namespace ParkNest.Domain.Common;

public enum UserRole
{
    Renter = 1,
    Host = 2,
    Both = 3,
    Admin = 4
}

public enum VehicleType
{
    TwoWheeler = 1,
    FourWheeler = 2
}

public enum KycStatus
{
    NotStarted = 0,
    Pending = 1,
    Verified = 2,
    Rejected = 3
}

public enum SpaceStatus
{
    Draft = 0,
    Published = 1,
    Paused = 2,
    Delisted = 3
}

public enum BookingStatus
{
    /// <summary>Credits reserved, session has not started yet.</summary>
    Held = 0,

    /// <summary>Renter has checked in; the meter is running.</summary>
    Active = 1,

    /// <summary>Session ended and fully settled.</summary>
    Completed = 2,

    /// <summary>Session ended but the renter could not cover the overstay; access is restricted until resolved.</summary>
    InViolation = 3,

    Disputed = 4,
    Cancelled = 5
}

/// <summary>How the entry/exit timestamps driving billing were captured (PRD §13).</summary>
public enum DetectionMethod
{
    /// <summary>Tier 1 — renter tapped "I've parked" / "I'm leaving".</summary>
    AppConfirmed = 1,

    /// <summary>Tier 2 — QR scan corroborated by a GPS geofence check.</summary>
    QrGeofence = 2,

    /// <summary>Tier 3 — number-plate recognition or an occupancy sensor.</summary>
    AnprSensor = 3,

    /// <summary>Resolved by a human after a dispute.</summary>
    AdminOverride = 4
}

/// <summary>
/// The buckets money can sit in. The first three are per-wallet; the rest are platform-level
/// system accounts that give every transaction a balancing counterparty.
/// </summary>
public enum LedgerAccountType
{
    /// <summary>Renter credits available to book with.</summary>
    Spendable = 1,

    /// <summary>Renter credits reserved against an open booking.</summary>
    Held = 2,

    /// <summary>Host credits earned and awaiting cash-out.</summary>
    Earning = 3,

    /// <summary>Platform commission income.</summary>
    PlatformRevenue = 10,

    /// <summary>Real money entering the system via a recharge.</summary>
    ExternalFunding = 11,

    /// <summary>Real money leaving the system via a host payout.</summary>
    ExternalPayout = 12
}

public enum LedgerTransactionType
{
    Recharge = 1,
    Hold = 2,
    ReleaseHold = 3,
    OverstayDebit = 4,
    Settlement = 5,
    Payout = 6,
    Refund = 7,
    AdminAdjustment = 8
}

public enum EntryDirection
{
    /// <summary>Value leaving the account.</summary>
    Debit = 1,

    /// <summary>Value entering the account.</summary>
    Credit = 2
}

public enum PayoutStatus
{
    Requested = 0,
    Processing = 1,
    Paid = 2,
    Failed = 3
}

public enum DisputeStatus
{
    Open = 0,
    UnderReview = 1,
    Resolved = 2,
    Rejected = 3
}

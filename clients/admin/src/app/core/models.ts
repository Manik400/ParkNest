// Mirrors the DTOs in ParkNest.Application. Kept hand-written rather than generated so the shape
// the UI depends on is reviewable in a diff; if this drifts from the API it is a compile error at
// the call site, not a silent undefined at runtime.

export type UserRole = 'Renter' | 'Host' | 'Both' | 'Admin';

export type VehicleType = 'TwoWheeler' | 'FourWheeler';

export type BookingStatus =
  | 'Held'
  | 'Active'
  | 'Completed'
  | 'InViolation'
  | 'Disputed'
  | 'Cancelled';

export type SpaceStatus = 'Draft' | 'Published' | 'Paused' | 'Delisted';

export interface OtpChallenge {
  expiresAt: string;
  /** Present only outside Production, so the app can be driven without an SMS gateway. */
  devCode: string | null;
}

export interface AuthResult {
  accessToken: string;
  expiresAt: string;
  userId: string;
  role: UserRole;
  isNewUser: boolean;
  refreshToken: string;
  refreshExpiresAt: string;
}

export interface Wallet {
  id: string;
  userId: string;
  spendable: number;
  held: number;
  earning: number;
}

export interface Reconciliation {
  stored: Wallet;
  replayedSpendable: number;
  replayedHeld: number;
  replayedEarning: number;
  /** False means the ledger and the cached balances have diverged — treat as an incident. */
  matches: boolean;
}

export interface LedgerEntrySummary {
  transactionId: string;
  /** The id a person quotes: TXN-… Printed on receipts, shown on every wallet line. */
  reference: string;
  /** Set when this transaction undoes an earlier one — the hold a release returns, the settlement a dispute adjusts. */
  revertsReference: string | null;
  transactionType: string;
  account: string;
  direction: 'Debit' | 'Credit';
  amount: number;
  bookingId: string | null;
  description: string | null;
  createdAt: string;
}

export interface BookingSummary {
  id: string;
  parkingSpaceId: string;
  spaceTitle: string;
  spaceAddress: string;
  startTime: string;
  expectedEndTime: string;
  actualEndTime: string | null;
  ratePerHour: number;
  holdAmount: number;
  settledAmount: number;
  status: BookingStatus;
  /** The previous car had not left when this slot came due. Cancelling it is free. */
  slotBlocked: boolean;
}

export interface BookingDetail {
  summary: BookingSummary;
  renterId: string;
  hostId: string;
  vehicleId: string;
  vehiclePlate: string;
  actualStartTime: string | null;
  bookedMinutes: number;
  billedMinutes: number | null;
  overstayAmount: number;
  platformFee: number;
  shortfallAmount: number;
  startDetectionMethod: string | null;
  endDetectionMethod: string | null;
  /** The over-running session that was still in the space, if there was one. */
  blockedByBookingId: string | null;
  blockedAt: string | null;
  ledgerEntries: LedgerEntrySummary[];
}

export interface ListingSummary {
  id: string;
  title: string;
  addressLine: string;
  city: string;
  pricePerHour: number;
  status: SpaceStatus;
  activeBookings: number;
}

export interface AvailabilityWindowView {
  /** The day's name ("Monday"): enums cross the wire as names. */
  dayOfWeek: number | string;
  startTime: string;
  endTime: string;
}

export interface ListingDetail {
  summary: ListingSummary;
  latitude: number;
  longitude: number;
  zone: string | null;
  timeZoneId: string;
  supportedVehicleTypes: string[];
  availabilityWindows: AvailabilityWindowView[];
  photoUrls: string[];
}

export interface ListingPhoto {
  id: string;
  url: string;
  sortOrder: number;
}

export interface Vehicle {
  id: string;
  plateNumber: string;
  type: VehicleType;
}

export interface NearbySpace {
  id: string;
  title: string;
  addressLine: string;
  city: string;
  latitude: number;
  longitude: number;
  pricePerHour: number;
  distanceMetres: number;
  /** The listing's first photo, or null when the host has not uploaded one yet. */
  photoUrl: string | null;
}

export interface BookingQuote {
  parkingSpaceId: string;
  startTime: string;
  endTime: string;
  billedMinutes: number;
  ratePerHour: number;
  amount: number;
  overstayRatePerHour: number;
  canBook: boolean;
  /** Why it cannot be booked, or null when it can. */
  unavailable: string | null;
}

export interface AvailabilityWindowRequest {
  dayOfWeek: number;
  /** "HH:mm:ss". Equal start and end means open 24 hours. */
  startTime: string;
  endTime: string;
}

export interface CreateListingRequest {
  title: string;
  addressLine: string;
  city: string;
  zone: string | null;
  latitude: number;
  longitude: number;
  pricePerHour: number;
  supportedVehicleTypes: VehicleType[];
  availabilityWindows: AvailabilityWindowRequest[];
  timeZoneId: string;
}

export interface StartPaymentResult {
  orderId: string;
  providerOrderId: string;
  amount: number;
  /** JSON describing the checkout. See CheckoutPayload for the shape we read. */
  checkoutPayload: string;
}

/**
 * What we parse out of `checkoutPayload`. Every gateway sends a `checkout_url` to open: the
 * sandbox's page, the provider's hosted page, or an API page that opens the provider's sheet. So
 * the client never needs a provider SDK. A relative URL is relative to the API.
 */
export interface CheckoutPayload {
  provider: string;
  order_id: string;
  checkout_url?: string;
  amount: number;
  currency: string;
}

export type PaymentOrderStatus = 'Created' | 'Paid' | 'Failed' | 'Cancelled';

/** The signed-in account. `phone` and `email` are sign-in identities; `paymentPhone` only goes to the gateway. */
export interface Profile {
  id: string;
  fullName: string;
  phone: string | null;
  email: string | null;
  paymentPhone: string | null;
  role: string;
  kycStatus: string;
}

export interface UpdateProfileRequest {
  fullName?: string;
  paymentPhone?: string;
}

export interface PaymentOrderView {
  orderId: string;
  providerOrderId: string;
  amount: number;
  currency: string;
  status: PaymentOrderStatus;
  failureReason: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface CityPricingConfig {
  id: string;
  city: string;
  zone: string | null;
  vehicleType: VehicleType;
  minPricePerHour: number;
  maxPricePerHour: number;
  overstayMultiplier: number;
  isActive: boolean;
  updatedAt: string;
}

export interface UpsertBandRequest {
  city: string;
  zone: string | null;
  vehicleType: VehicleType;
  minPricePerHour: number;
  maxPricePerHour: number;
  overstayMultiplier: number;
  isActive: boolean;
  /** Free text, asked for on every edit — it is the part the audit row cannot reconstruct. */
  reason: string | null;
}

export type PricingBandChangeKind = 'Created' | 'Updated' | 'Deactivated' | 'Reactivated';

/**
 * One recorded edit to a band. A band changes without a deploy and therefore without a commit,
 * so this is the only record of how a city's price ceiling got where it is.
 */
export interface PricingBandChange {
  id: string;
  cityPricingConfigId: string;
  city: string;
  zone: string | null;
  vehicleType: VehicleType;
  kind: PricingBandChangeKind;
  /** Null on a Created row — there was no band before it. */
  previousMinPricePerHour: number | null;
  previousMaxPricePerHour: number | null;
  previousOverstayMultiplier: number | null;
  previousIsActive: boolean | null;
  minPricePerHour: number;
  maxPricePerHour: number;
  overstayMultiplier: number;
  isActive: boolean;
  changedByUserId: string;
  reason: string | null;
  changedAt: string;
}

export interface SetBandActiveRequest {
  isActive: boolean;
  reason: string | null;
}

/** RFC 7807 problem details, which is what the API returns for every non-2xx. */
export interface ProblemDetails {
  status: number;
  title: string;
  detail: string | null;
  type: string | null;
}

export const DAY_NAMES = [
  'Sunday',
  'Monday',
  'Tuesday',
  'Wednesday',
  'Thursday',
  'Friday',
  'Saturday',
] as const;

export type DisputeStatus = 'Open' | 'UnderReview' | 'Resolved' | 'Rejected';

export interface DisputeEvidence {
  id: string;
  url: string;
  note: string | null;
}

export interface Dispute {
  disputeId: string;
  bookingId: string;
  raisedByUserId: string;
  renterId: string;
  hostId: string;
  reason: string;
  status: DisputeStatus;
  resolution: string | null;
  /** Credits actually moved when the dispute was upheld. Null when it was settled without money. */
  adjustmentAmount: number | null;
  adjustmentTransactionId: string | null;
  evidence: DisputeEvidence[];
  createdAt: string;
  resolvedAt: string | null;
}

export type PayoutStatus = 'Requested' | 'Processing' | 'Paid' | 'Failed';

export interface Payout {
  payoutId: string;
  hostId: string;
  /** Null for a host who signed up by email. */
  hostPhone: string | null;
  hostName: string;
  amount: number;
  status: PayoutStatus;
  providerReference: string | null;
  failureReason: string | null;
  createdAt: string;
  completedAt: string | null;
}

/** What cancelling a booking would cost right now. The policy lives on the server. */
export interface CancellationTerms {
  holdAmount: number;
  fee: number;
  refund: number;
  isFree: boolean;
  /** After this moment cancelling starts costing something. */
  freeUntil: string;
  /**
   * Free because the space was still occupied, rather than because there is time in hand. The
   * distinction is the whole message: "you got lucky" and "we could not give you the space" read
   * very differently to somebody standing next to somebody else's car.
   */
  slotBlocked: boolean;
}

export type KycStatus = 'NotStarted' | 'Pending' | 'Verified' | 'Rejected';

/**
 * One attempt by a host to prove who they are.
 *
 * The document number is not here and never will be: the server keeps the last four characters
 * for a human to match against the photograph, and a keyed hash for spotting one document used by
 * two accounts. `previousRejections` and `otherAccountsWithThisDocument` are the two things a
 * reviewer cannot see from a single row and needs before approving money out.
 */
export interface KycSubmission {
  id: string;
  userId: string;
  legalName: string;
  documentType: string;
  documentLast4: string;
  documentPhotoUrl: string | null;
  payoutAccountLast4: string | null;
  status: KycStatus;
  submittedAt: string;
  reviewedAt: string | null;
  rejectionReason: string | null;
  userPhone: string | null;
  previousRejections: number;
  otherAccountsWithThisDocument: number;
}

export interface Rating {
  id: string;
  bookingId: string;
  toUserId: string;
  score: number;
  comment: string | null;
  createdAt: string;
}

/**
 * What a user's counterparties have said about them.
 *
 * `averageScore` is null for somebody nobody has rated, and must stay null on the way to a screen:
 * rendering it as 0.0 would tell a reviewer that a brand-new host is terrible, which is the
 * opposite of what no ratings means. `trustScore` is the different thing the platform acts on —
 * it starts from good faith and moves on conduct.
 */
export interface Reputation {
  userId: string;
  trustScore: number;
  averageScore: number | null;
  ratingCount: number;
  recent: Rating[];
}


// --- Analytics (owner only) -------------------------------------------------

export interface AnalyticsTotals {
  /** Sessions started: someone opened the site. The hit counter. */
  visits: number;
  pageViews: number;
  /** Distinct browsers. Always at or below `visits`, and the honest headline figure. */
  visitors: number;
  searches: number;
  signIns: number;
  signUps: number;
  bookings: number;
  bookingValue: number;
  paymentAttempts: number;
  paymentsSucceeded: number;
  paymentsFailed: number;
  paymentValue: number;
}

export interface AnalyticsDay {
  date: string;
  visits: number;
  pageViews: number;
  visitors: number;
  bookings: number;
  paymentAttempts: number;
  paymentsSucceeded: number;
}

export interface AnalyticsPage {
  path: string;
  views: number;
}

export interface AnalyticsCounter {
  name: string;
  count: number;
  lastSeen: string;
}

export interface AnalyticsRecentEvent {
  name: string;
  occurredAt: string;
  source: string;
  path: string | null;
  amount: number | null;
  detail: string | null;
  signedIn: boolean;
}

export interface AnalyticsSummary {
  from: string;
  to: string;
  days: number;
  totals: AnalyticsTotals;
  /** One entry per day in the window, quiet days included, oldest first. */
  daily: AnalyticsDay[];
  topPages: AnalyticsPage[];
  /** Every event name that fired in the window, busiest first. */
  counters: AnalyticsCounter[];
  recent: AnalyticsRecentEvent[];
  allTimeVisits: number;
  allTimePageViews: number;
  allTimeVisitors: number;
  allTimeEvents: number;
}

// --- Cities ------------------------------------------------------------------

export interface City {
  name: string;
  state: string;
  latitude: number;
  longitude: number;
  timeZoneId: string;
  /** An active price band exists, so a listing here can actually be published. */
  hasPricing: boolean;
}

export interface CityRequestSummary {
  city: string;
  count: number;
  firstAskedAt: string;
  lastAskedAt: string;
  notes: string[];
}

// --- Platform (admin) --------------------------------------------------------

export interface PlatformRevenue {
  total: number;
  thisMonth: number;
  earned: number;
  givenBack: number;
  settlements: number;
}

export interface DataResetInfo {
  allowed: boolean;
  confirmationPhrase: string;
}

export interface DataResetResult {
  rowsRemoved: Record<string, number>;
  kept: string[];
}

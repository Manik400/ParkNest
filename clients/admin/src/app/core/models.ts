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
  dayOfWeek: number;
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

export interface Vehicle {
  id: string;
  plateNumber: string;
  type: VehicleType;
}

export interface NearbySpace {
  id: string;
  title: string;
  addressLine: string;
  latitude: number;
  longitude: number;
  pricePerHour: number;
  distanceMetres: number;
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
  /** Opaque JSON the gateway's checkout SDK consumes. See CheckoutPayload for the shape we read. */
  checkoutPayload: string;
}

/**
 * What we parse out of `checkoutPayload`. The sandbox gateway hands us a URL to redirect to; a
 * real aggregator hands us keys for its JS SDK instead, so `checkout_url` is what tells the two
 * apart at runtime.
 */
export interface CheckoutPayload {
  provider: string;
  order_id: string;
  checkout_url?: string;
  amount: number;
  currency: string;
}

export type PaymentOrderStatus = 'Created' | 'Paid' | 'Failed' | 'Cancelled';

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
  hostPhone: string;
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


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

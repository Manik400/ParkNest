import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../environments/environment';
import {
  BookingDetail,
  BookingQuote,
  BookingStatus,
  BookingSummary,
  CancellationTerms,
  CityPricingConfig,
  CreateListingRequest,
  Dispute,
  KycStatus,
  KycSubmission,
  LedgerEntrySummary,
  ListingDetail,
  ListingSummary,
  NearbySpace,
  PaymentOrderView,
  Payout,
  PayoutStatus,
  Reconciliation,
  Reputation,
  StartPaymentResult,
  UpsertBandRequest,
  Vehicle,
  Wallet,
} from './models';

/** One place that knows the API's shape, so a route change is a single edit. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = environment.apiBaseUrl;

  // --- Wallet ---------------------------------------------------------------

  myWallet(): Observable<Wallet> {
    return this.http.get<Wallet>(`${this.base}/api/wallets/me`);
  }

  myTransactions(limit = 50, offset = 0): Observable<LedgerEntrySummary[]> {
    return this.http.get<LedgerEntrySummary[]>(`${this.base}/api/wallets/me/transactions`, {
      params: new HttpParams().set('limit', limit).set('offset', offset),
    });
  }

  /** Admin/support view of any wallet. */
  walletFor(userId: string): Observable<Wallet> {
    return this.http.get<Wallet>(`${this.base}/api/wallets/${userId}`);
  }

  /** Replays a wallet from its ledger entries to prove the stored balances still agree. */
  reconcile(userId: string): Observable<Reconciliation> {
    return this.http.get<Reconciliation>(`${this.base}/api/wallets/${userId}/reconciliation`);
  }

  recharge(userId: string, amount: number, idempotencyKey: string): Observable<Wallet> {
    return this.http.post<Wallet>(`${this.base}/api/wallets/${userId}/recharge`, {
      amount,
      idempotencyKey,
    });
  }

  // --- Payments -------------------------------------------------------------

  /**
   * Starts a credit purchase. Issues no credits - the wallet only moves when the gateway's
   * signed webhook confirms the money actually arrived.
   */
  startPayment(amount: number): Observable<StartPaymentResult> {
    return this.http.post<StartPaymentResult>(`${this.base}/api/payments/orders`, { amount });
  }

  /**
   * Where an order stands. Polled after checkout rather than trusting the return trip - coming
   * back from the gateway proves the user pressed a button, not that the money arrived.
   */
  paymentOrder(orderId: string): Observable<PaymentOrderView> {
    return this.http.get<PaymentOrderView>(`${this.base}/api/payments/orders/${orderId}`);
  }

  // --- Vehicles -------------------------------------------------------------

  myVehicles(): Observable<Vehicle[]> {
    return this.http.get<Vehicle[]>(`${this.base}/api/vehicles/me`);
  }

  addVehicle(plateNumber: string, type: string): Observable<Vehicle> {
    return this.http.post<Vehicle>(`${this.base}/api/vehicles`, { plateNumber, type });
  }

  removeVehicle(vehicleId: string): Observable<unknown> {
    return this.http.delete(`${this.base}/api/vehicles/${vehicleId}`);
  }

  // --- Bookings -------------------------------------------------------------

  /** Prices a prospective booking without reserving anything. */
  quote(spaceId: string, startTime: string, durationMinutes: number): Observable<BookingQuote> {
    return this.http.get<BookingQuote>(`${this.base}/api/bookings/quote`, {
      params: new HttpParams()
        .set('spaceId', spaceId)
        .set('startTime', startTime)
        .set('durationMinutes', durationMinutes),
    });
  }

  book(
    parkingSpaceId: string,
    vehicleId: string,
    startTime: string,
    durationMinutes: number,
    idempotencyKey: string,
  ): Observable<{ id: string; holdAmount: number; status: string }> {
    return this.http.post<{ id: string; holdAmount: number; status: string }>(
      `${this.base}/api/bookings`,
      { parkingSpaceId, vehicleId, startTime, durationMinutes, idempotencyKey },
    );
  }

  startSession(bookingId: string): Observable<unknown> {
    return this.http.post(`${this.base}/api/bookings/${bookingId}/start`, { method: 'AppConfirmed' });
  }

  endSession(bookingId: string): Observable<{
    billedMinutes: number;
    totalCharged: number;
    releasedToRenter: number;
    hostCredited: number;
    platformFee: number;
    shortfall: number;
  }> {
    return this.http.post<{
      billedMinutes: number;
      totalCharged: number;
      releasedToRenter: number;
      hostCredited: number;
      platformFee: number;
      shortfall: number;
    }>(`${this.base}/api/bookings/${bookingId}/end`, { method: 'AppConfirmed' });
  }

  /** What cancelling would cost, without cancelling. */
  cancellationTerms(bookingId: string): Observable<CancellationTerms> {
    return this.http.get<CancellationTerms>(
      `${this.base}/api/bookings/${bookingId}/cancellation`,
    );
  }

  cancelBooking(bookingId: string): Observable<unknown> {
    return this.http.post(`${this.base}/api/bookings/${bookingId}/cancel`, {});
  }

  myBookings(status?: BookingStatus): Observable<BookingSummary[]> {
    return this.http.get<BookingSummary[]>(`${this.base}/api/bookings/me`, {
      params: status ? new HttpParams().set('status', status) : undefined,
    });
  }

  hostingBookings(status?: BookingStatus): Observable<BookingSummary[]> {
    return this.http.get<BookingSummary[]>(`${this.base}/api/bookings/hosting`, {
      params: status ? new HttpParams().set('status', status) : undefined,
    });
  }

  booking(bookingId: string): Observable<BookingDetail> {
    return this.http.get<BookingDetail>(`${this.base}/api/bookings/${bookingId}`);
  }

  // --- Listings -------------------------------------------------------------

  createListing(request: CreateListingRequest): Observable<{ id: string }> {
    return this.http.post<{ id: string }>(`${this.base}/api/listings`, request);
  }

  searchNearby(lat: number, lng: number, radiusMetres: number, maxPrice?: number): Observable<NearbySpace[]> {
    let params = new HttpParams()
      .set('lat', lat)
      .set('lng', lng)
      .set('radiusMetres', radiusMetres);
    if (maxPrice != null) {
      params = params.set('maxPricePerHour', maxPrice);
    }
    return this.http.get<NearbySpace[]>(`${this.base}/api/listings/nearby`, { params });
  }

  myListings(): Observable<ListingSummary[]> {
    return this.http.get<ListingSummary[]>(`${this.base}/api/listings/me`);
  }

  listing(spaceId: string): Observable<ListingDetail> {
    return this.http.get<ListingDetail>(`${this.base}/api/listings/${spaceId}`);
  }

  publishListing(spaceId: string): Observable<unknown> {
    return this.http.post(`${this.base}/api/listings/${spaceId}/publish`, {});
  }

  setListingStatus(spaceId: string, status: string): Observable<unknown> {
    return this.http.post(`${this.base}/api/listings/${spaceId}/status`, { status });
  }

  // --- Admin: pricing bands -------------------------------------------------

  pricingBands(city?: string): Observable<CityPricingConfig[]> {
    return this.http.get<CityPricingConfig[]>(`${this.base}/api/admin/pricing`, {
      params: city ? new HttpParams().set('city', city) : undefined,
    });
  }

  upsertBand(request: UpsertBandRequest): Observable<CityPricingConfig> {
    return this.http.put<CityPricingConfig>(`${this.base}/api/admin/pricing`, request);
  }

  // --- Disputes -------------------------------------------------------------

  myDisputes(onlyOpen = false): Observable<Dispute[]> {
    return this.http.get<Dispute[]>(`${this.base}/api/disputes/me`, {
      params: new HttpParams().set('onlyOpen', onlyOpen),
    });
  }

  raiseDispute(bookingId: string, reason: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.base}/api/disputes`, { bookingId, reason });
  }

  // --- Admin: disputes ------------------------------------------------------

  adminDisputes(onlyOpen = true): Observable<Dispute[]> {
    return this.http.get<Dispute[]>(`${this.base}/api/admin/disputes`, {
      params: new HttpParams().set('onlyOpen', onlyOpen),
    });
  }

  reviewDispute(disputeId: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.base}/api/admin/disputes/${disputeId}/review`, {});
  }

  /** Upholds the dispute. Any refund is posted as a new compensating transaction. */
  resolveDispute(
    disputeId: string,
    resolution: string,
    refundToRenter: number,
    chargedToPlatform: boolean,
  ): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.base}/api/admin/disputes/${disputeId}/resolve`, {
      resolution,
      refundToRenter,
      chargedToPlatform,
    });
  }

  rejectDispute(disputeId: string, resolution: string): Observable<Dispute> {
    return this.http.post<Dispute>(`${this.base}/api/admin/disputes/${disputeId}/reject`, {
      resolution,
    });
  }

  // --- Reputation -----------------------------------------------------------

  /** A user's ratings and trust score. Any signed-in caller may read one. */
  reputation(userId: string): Observable<Reputation> {
    return this.http.get<Reputation>(`${this.base}/api/ratings/users/${userId}`);
  }

  // --- Admin: identity verification -----------------------------------------

  /** The review queue. Pending by default; a status reads history instead. */
  kycQueue(status?: KycStatus): Observable<KycSubmission[]> {
    return this.http.get<KycSubmission[]>(`${this.base}/api/admin/kyc`, {
      params: status ? new HttpParams().set('status', status) : undefined,
    });
  }

  /** Approves the submission, which is what opens cash-out for that host. */
  verifyKyc(submissionId: string): Observable<KycSubmission> {
    return this.http.post<KycSubmission>(`${this.base}/api/admin/kyc/${submissionId}/verify`, {});
  }

  /** Refuses it. The reason is shown to the host, so it has to be actionable. */
  rejectKyc(submissionId: string, reason: string): Observable<KycSubmission> {
    return this.http.post<KycSubmission>(`${this.base}/api/admin/kyc/${submissionId}/reject`, {
      reason,
    });
  }

  // --- Admin: payouts -------------------------------------------------------

  payouts(status?: PayoutStatus): Observable<Payout[]> {
    return this.http.get<Payout[]>(`${this.base}/api/admin/payouts`, {
      params: status ? new HttpParams().set('status', status) : undefined,
    });
  }

  completePayout(payoutId: string, providerReference: string | null): Observable<Payout> {
    return this.http.post<Payout>(`${this.base}/api/admin/payouts/${payoutId}/complete`, {
      providerReference,
    });
  }

  /** Records a rejected transfer. The credits go back to the host via a compensating Refund. */
  failPayout(payoutId: string, reason: string): Observable<Payout> {
    return this.http.post<Payout>(`${this.base}/api/admin/payouts/${payoutId}/fail`, { reason });
  }
}

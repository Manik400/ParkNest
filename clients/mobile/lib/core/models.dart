/// Wire types, mirroring the API's DTOs.
///
/// Hand-written rather than generated: the surface is small enough that a build step would cost
/// more than it saves, and parsing by hand is where a contract change gets noticed.
///
/// Money arrives as a JSON number for a C# `decimal`. It is read through [_money] into a Dart
/// `double` — fine for displaying a balance, and never used to compute one. Every figure the user
/// acts on is calculated server-side against the ledger; the app only ever renders what it is told.
library;

import 'dart:convert';

double _money(Object? value) => (value as num?)?.toDouble() ?? 0;

DateTime? _dateOrNull(Object? value) =>
    value == null ? null : DateTime.parse(value as String).toLocal();

DateTime _date(Object? value) => DateTime.parse(value! as String).toLocal();

class OtpChallenge {
  const OtpChallenge({required this.expiresAt, this.devCode});

  factory OtpChallenge.fromJson(Map<String, dynamic> json) => OtpChallenge(
        expiresAt: _date(json['expiresAt']),
        devCode: json['devCode'] as String?,
      );

  final DateTime expiresAt;

  /// Only ever populated outside Production, where no SMS gateway is wired. Shown on the sign-in
  /// screen so the app can be driven without a phone that receives texts.
  final String? devCode;
}

class AuthResult {
  const AuthResult({
    required this.accessToken,
    required this.expiresAt,
    required this.userId,
    required this.role,
    required this.isNewUser,
    required this.refreshToken,
    required this.refreshExpiresAt,
  });

  factory AuthResult.fromJson(Map<String, dynamic> json) => AuthResult(
        accessToken: json['accessToken'] as String,
        expiresAt: _date(json['expiresAt']),
        userId: json['userId'] as String,
        role: json['role'] as String,
        isNewUser: json['isNewUser'] as bool? ?? false,
        refreshToken: json['refreshToken'] as String,
        refreshExpiresAt: _date(json['refreshExpiresAt']),
      );

  final String accessToken;
  final DateTime expiresAt;
  final String userId;
  final String role;
  final bool isNewUser;
  final String refreshToken;
  final DateTime refreshExpiresAt;
}

class Wallet {
  const Wallet({
    required this.id,
    required this.userId,
    required this.spendable,
    required this.held,
    required this.earning,
  });

  factory Wallet.fromJson(Map<String, dynamic> json) => Wallet(
        id: json['id'] as String,
        userId: json['userId'] as String,
        spendable: _money(json['spendable']),
        held: _money(json['held']),
        earning: _money(json['earning']),
      );

  /// Free to spend on a new booking.
  final double spendable;

  /// Reserved against open bookings. Not spendable, not yet the host's.
  final double held;

  /// Earned as a host, awaiting cash-out.
  final double earning;

  final String id;
  final String userId;
}

class LedgerEntrySummary {
  const LedgerEntrySummary({
    required this.transactionId,
    required this.transactionType,
    required this.account,
    required this.direction,
    required this.amount,
    required this.createdAt,
    this.bookingId,
    this.description,
  });

  factory LedgerEntrySummary.fromJson(Map<String, dynamic> json) => LedgerEntrySummary(
        transactionId: json['transactionId'] as String,
        transactionType: json['transactionType'] as String,
        account: json['account'] as String,
        direction: json['direction'] as String,
        amount: _money(json['amount']),
        bookingId: json['bookingId'] as String?,
        description: json['description'] as String?,
        createdAt: _date(json['createdAt']),
      );

  final String transactionId;
  final String transactionType;
  final String account;

  /// `Credit` when value entered the account, `Debit` when it left.
  final String direction;

  final double amount;
  final String? bookingId;
  final String? description;
  final DateTime createdAt;

  bool get isCredit => direction == 'Credit';
}

/// The signed-in account. `phone` and `email` are sign-in identities; `paymentPhone` only goes
/// to the payment gateway, which insists on a number, and needs no verification code.
class Profile {
  const Profile({
    required this.id,
    required this.fullName,
    required this.phone,
    required this.email,
    required this.paymentPhone,
    required this.role,
    required this.kycStatus,
  });

  factory Profile.fromJson(Map<String, dynamic> json) => Profile(
        id: json['id'] as String,
        fullName: json['fullName'] as String,
        phone: json['phone'] as String?,
        email: json['email'] as String?,
        paymentPhone: json['paymentPhone'] as String?,
        role: json['role'] as String,
        kycStatus: json['kycStatus'] as String,
      );

  final String id;
  final String fullName;
  final String? phone;
  final String? email;
  final String? paymentPhone;
  final String role;
  final String kycStatus;
}

class Vehicle {
  const Vehicle({required this.id, required this.plateNumber, required this.type});

  factory Vehicle.fromJson(Map<String, dynamic> json) => Vehicle(
        id: json['id'] as String,
        plateNumber: json['plateNumber'] as String,
        type: json['type'] as String,
      );

  final String id;
  final String plateNumber;
  final String type;
}

class NearbySpace {
  const NearbySpace({
    required this.id,
    required this.title,
    required this.addressLine,
    required this.latitude,
    required this.longitude,
    required this.pricePerHour,
    required this.distanceMetres,
  });

  factory NearbySpace.fromJson(Map<String, dynamic> json) => NearbySpace(
        id: json['id'] as String,
        title: json['title'] as String,
        addressLine: json['addressLine'] as String,
        latitude: (json['latitude'] as num).toDouble(),
        longitude: (json['longitude'] as num).toDouble(),
        pricePerHour: _money(json['pricePerHour']),
        distanceMetres: (json['distanceMetres'] as num).toDouble(),
      );

  final String id;
  final String title;
  final String addressLine;
  final double latitude;
  final double longitude;
  final double pricePerHour;
  final double distanceMetres;
}

class BookingQuote {
  const BookingQuote({
    required this.parkingSpaceId,
    required this.startTime,
    required this.endTime,
    required this.billedMinutes,
    required this.ratePerHour,
    required this.amount,
    required this.overstayRatePerHour,
    required this.canBook,
    this.unavailable,
  });

  factory BookingQuote.fromJson(Map<String, dynamic> json) => BookingQuote(
        parkingSpaceId: json['parkingSpaceId'] as String,
        startTime: _date(json['startTime']),
        endTime: _date(json['endTime']),
        billedMinutes: json['billedMinutes'] as int,
        ratePerHour: _money(json['ratePerHour']),
        amount: _money(json['amount']),
        overstayRatePerHour: _money(json['overstayRatePerHour']),
        canBook: json['canBook'] as bool,
        unavailable: json['unavailable'] as String?,
      );

  final String parkingSpaceId;
  final DateTime startTime;
  final DateTime endTime;
  final int billedMinutes;
  final double ratePerHour;
  final double amount;

  /// What an hour past the booked window costs. Shown up front so an overstay is never a surprise.
  final double overstayRatePerHour;

  final bool canBook;

  /// Why it cannot be booked, or null when it can.
  final String? unavailable;
}

class BookingSummary {
  const BookingSummary({
    required this.id,
    required this.parkingSpaceId,
    required this.spaceTitle,
    required this.spaceAddress,
    required this.startTime,
    required this.expectedEndTime,
    required this.ratePerHour,
    required this.holdAmount,
    required this.settledAmount,
    required this.status,
    required this.slotBlocked,
    this.actualEndTime,
  });

  factory BookingSummary.fromJson(Map<String, dynamic> json) => BookingSummary(
        id: json['id'] as String,
        parkingSpaceId: json['parkingSpaceId'] as String,
        spaceTitle: json['spaceTitle'] as String,
        spaceAddress: json['spaceAddress'] as String,
        startTime: _date(json['startTime']),
        expectedEndTime: _date(json['expectedEndTime']),
        actualEndTime: _dateOrNull(json['actualEndTime']),
        ratePerHour: _money(json['ratePerHour']),
        holdAmount: _money(json['holdAmount']),
        settledAmount: _money(json['settledAmount']),
        status: json['status'] as String,
        // Defaulted rather than required so an app build newer than the API it is pointed at
        // degrades to "not blocked" instead of failing to parse the booking list entirely.
        slotBlocked: json['slotBlocked'] as bool? ?? false,
      );

  final String id;
  final String parkingSpaceId;
  final String spaceTitle;
  final String spaceAddress;
  final DateTime startTime;
  final DateTime expectedEndTime;
  final DateTime? actualEndTime;
  final double ratePerHour;
  final double holdAmount;
  final double settledAmount;
  final String status;

  /// The previous car had not left when this slot came due. Cancelling it is free.
  final bool slotBlocked;

  bool get isOpen => status == 'Held' || status == 'Active';
}

class BookingDetail {
  const BookingDetail({
    required this.summary,
    required this.renterId,
    required this.hostId,
    required this.vehicleId,
    required this.vehiclePlate,
    required this.bookedMinutes,
    required this.overstayAmount,
    required this.platformFee,
    required this.shortfallAmount,
    required this.ledgerEntries,
    this.actualStartTime,
    this.billedMinutes,
    this.startDetectionMethod,
    this.endDetectionMethod,
    this.blockedByBookingId,
    this.blockedAt,
  });

  factory BookingDetail.fromJson(Map<String, dynamic> json) => BookingDetail(
        summary: BookingSummary.fromJson(json['summary'] as Map<String, dynamic>),
        renterId: json['renterId'] as String,
        hostId: json['hostId'] as String,
        vehicleId: json['vehicleId'] as String,
        vehiclePlate: json['vehiclePlate'] as String,
        actualStartTime: _dateOrNull(json['actualStartTime']),
        bookedMinutes: json['bookedMinutes'] as int,
        billedMinutes: json['billedMinutes'] as int?,
        overstayAmount: _money(json['overstayAmount']),
        platformFee: _money(json['platformFee']),
        shortfallAmount: _money(json['shortfallAmount']),
        startDetectionMethod: json['startDetectionMethod'] as String?,
        endDetectionMethod: json['endDetectionMethod'] as String?,
        blockedByBookingId: json['blockedByBookingId'] as String?,
        blockedAt: _dateOrNull(json['blockedAt']),
        ledgerEntries: (json['ledgerEntries'] as List<dynamic>)
            .map((e) => LedgerEntrySummary.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  final BookingSummary summary;
  final String renterId;
  final String hostId;
  final String vehicleId;
  final String vehiclePlate;
  final DateTime? actualStartTime;
  final int bookedMinutes;
  final int? billedMinutes;
  final double overstayAmount;
  final double platformFee;

  /// Credits owed at settlement that the renter could not cover. Non-zero means a violation.
  final double shortfallAmount;

  final String? startDetectionMethod;
  final String? endDetectionMethod;

  /// The over-running session that was still in the space when this slot came due, if there was
  /// one. Its presence is what makes cancelling free.
  final String? blockedByBookingId;
  final DateTime? blockedAt;

  final List<LedgerEntrySummary> ledgerEntries;
}

class SessionOutcome {
  const SessionOutcome({
    required this.billedMinutes,
    required this.totalCharged,
    required this.releasedToRenter,
    required this.hostCredited,
    required this.platformFee,
    required this.shortfall,
  });

  factory SessionOutcome.fromJson(Map<String, dynamic> json) => SessionOutcome(
        billedMinutes: json['billedMinutes'] as int,
        totalCharged: _money(json['totalCharged']),
        releasedToRenter: _money(json['releasedToRenter']),
        hostCredited: _money(json['hostCredited']),
        platformFee: _money(json['platformFee']),
        shortfall: _money(json['shortfall']),
      );

  final int billedMinutes;
  final double totalCharged;
  final double releasedToRenter;
  final double hostCredited;
  final double platformFee;
  final double shortfall;
}

class ListingSummary {
  const ListingSummary({
    required this.id,
    required this.title,
    required this.addressLine,
    required this.city,
    required this.pricePerHour,
    required this.status,
    required this.activeBookings,
  });

  factory ListingSummary.fromJson(Map<String, dynamic> json) => ListingSummary(
        id: json['id'] as String,
        title: json['title'] as String,
        addressLine: json['addressLine'] as String,
        city: json['city'] as String,
        pricePerHour: _money(json['pricePerHour']),
        status: json['status'] as String,
        activeBookings: json['activeBookings'] as int? ?? 0,
      );

  final String id;
  final String title;
  final String addressLine;
  final String city;
  final double pricePerHour;
  final String status;
  final int activeBookings;
}

class StartPaymentResult {
  const StartPaymentResult({
    required this.orderId,
    required this.providerOrderId,
    required this.amount,
    required this.checkoutPayload,
  });

  factory StartPaymentResult.fromJson(Map<String, dynamic> json) => StartPaymentResult(
        orderId: json['orderId'] as String,
        providerOrderId: json['providerOrderId'] as String,
        amount: _money(json['amount']),
        checkoutPayload: json['checkoutPayload'] as String,
      );

  final String orderId;
  final String providerOrderId;
  final double amount;

  /// JSON describing the checkout. Every gateway the API offers puts a `checkout_url` in it — the
  /// sandbox's page, the provider's hosted page, or an API page that opens the provider's sheet —
  /// so the app never needs a provider SDK. An envelope, never a bare URL — see [checkoutUrl].
  final String checkoutPayload;

  /// The page to open for checkout, or null for a payload that carries none.
  ///
  /// [apiBaseUrl] is needed because the URL may be relative to the API (the sandbox's is).
  /// Treating [checkoutPayload] as a link directly is the obvious mistake and fails at launch with
  /// nothing useful to show the user.
  String? checkoutUrl(String apiBaseUrl) {
    final Object? decoded;

    try {
      decoded = jsonDecode(checkoutPayload);
    } on FormatException {
      return null;
    }

    if (decoded is! Map) return null;

    final url = decoded['checkout_url'];
    if (url is! String || url.isEmpty) return null;

    return url.startsWith('http') ? url : '$apiBaseUrl$url';
  }
}

class PaymentOrderView {
  const PaymentOrderView({
    required this.orderId,
    required this.amount,
    required this.currency,
    required this.status,
    required this.createdAt,
    this.failureReason,
    this.completedAt,
  });

  factory PaymentOrderView.fromJson(Map<String, dynamic> json) => PaymentOrderView(
        orderId: json['orderId'] as String,
        amount: _money(json['amount']),
        currency: json['currency'] as String? ?? 'INR',
        status: json['status'] as String,
        failureReason: json['failureReason'] as String?,
        createdAt: _date(json['createdAt']),
        completedAt: _dateOrNull(json['completedAt']),
      );

  final String orderId;
  final double amount;
  final String currency;
  final String status;
  final String? failureReason;
  final DateTime createdAt;
  final DateTime? completedAt;

  bool get isSettled => status == 'Paid' || status == 'Failed' || status == 'Cancelled';
}

class DisputeEvidence {
  const DisputeEvidence({required this.id, required this.url, this.note});

  factory DisputeEvidence.fromJson(Map<String, dynamic> json) => DisputeEvidence(
        id: json['id'] as String,
        url: json['url'] as String,
        note: json['note'] as String?,
      );

  final String id;
  final String url;
  final String? note;
}

class Dispute {
  const Dispute({
    required this.disputeId,
    required this.bookingId,
    required this.raisedByUserId,
    required this.renterId,
    required this.hostId,
    required this.reason,
    required this.status,
    required this.evidence,
    required this.createdAt,
    this.resolution,
    this.adjustmentAmount,
    this.resolvedAt,
  });

  factory Dispute.fromJson(Map<String, dynamic> json) => Dispute(
        disputeId: json['disputeId'] as String,
        bookingId: json['bookingId'] as String,
        raisedByUserId: json['raisedByUserId'] as String,
        renterId: json['renterId'] as String,
        hostId: json['hostId'] as String,
        reason: json['reason'] as String,
        status: json['status'] as String,
        resolution: json['resolution'] as String?,
        adjustmentAmount:
            json['adjustmentAmount'] == null ? null : _money(json['adjustmentAmount']),
        evidence: (json['evidence'] as List<dynamic>? ?? const [])
            .map((e) => DisputeEvidence.fromJson(e as Map<String, dynamic>))
            .toList(),
        createdAt: _date(json['createdAt']),
        resolvedAt: _dateOrNull(json['resolvedAt']),
      );

  final String disputeId;
  final String bookingId;
  final String raisedByUserId;
  final String renterId;
  final String hostId;
  final String reason;
  final String status;
  final String? resolution;

  /// Credits actually moved when it was upheld. Null when it was settled without money.
  final double? adjustmentAmount;

  final List<DisputeEvidence> evidence;
  final DateTime createdAt;
  final DateTime? resolvedAt;

  bool get isDecided => status == 'Resolved' || status == 'Rejected';
}

/// A new listing, on its way out rather than in.
///
/// Availability is required in practice: the API refuses to publish a space with no windows,
/// because an always-closed pin on the map is worse than no pin at all.
class CreateListingRequest {
  const CreateListingRequest({
    required this.title,
    required this.addressLine,
    required this.city,
    required this.latitude,
    required this.longitude,
    required this.pricePerHour,
    required this.supportedVehicleTypes,
    required this.availabilityWindows,
    this.zone,
    this.timeZoneId = 'Asia/Kolkata',
  });

  final String title;
  final String addressLine;
  final String city;
  final String? zone;
  final double latitude;
  final double longitude;
  final double pricePerHour;
  final List<String> supportedVehicleTypes;
  final List<AvailabilityWindowRequest> availabilityWindows;

  /// The zone the windows are expressed in. Windows are wall-clock — "open 09:00 to 17:00" — so
  /// without this a space in another city would silently keep the server's hours.
  final String timeZoneId;

  Map<String, dynamic> toJson() => {
        'title': title,
        'addressLine': addressLine,
        'city': city,
        'zone': zone,
        'latitude': latitude,
        'longitude': longitude,
        'pricePerHour': pricePerHour,
        'supportedVehicleTypes': supportedVehicleTypes,
        'availabilityWindows': availabilityWindows.map((w) => w.toJson()).toList(),
        'timeZoneId': timeZoneId,
      };
}

/// One opening window on one day.
///
/// An end earlier than the start runs overnight (22:00 to 06:00). Equal to it means the full
/// twenty-four hours, which is how a 24/7 basement is expressed — a zero-length window is said by
/// having no window at all.
class AvailabilityWindowRequest {
  const AvailabilityWindowRequest({
    required this.dayOfWeek,
    required this.startTime,
    required this.endTime,
  });

  /// The .NET day name the API expects: Sunday through Saturday.
  final String dayOfWeek;

  final Duration startTime;
  final Duration endTime;

  static String _hhmmss(Duration value) {
    final hours = value.inHours.toString().padLeft(2, '0');
    final minutes = (value.inMinutes % 60).toString().padLeft(2, '0');
    return '$hours:$minutes:00';
  }

  Map<String, dynamic> toJson() => {
        'dayOfWeek': dayOfWeek,
        'startTime': _hhmmss(startTime),
        'endTime': _hhmmss(endTime),
      };
}

/// What cancelling a booking would cost right now.
///
/// Fetched rather than computed on the handset: the policy is configuration on the server, and a
/// second copy here would go stale the moment ops changed it — while confidently telling the user
/// a number that is not what they will be charged.
class CancellationTerms {
  const CancellationTerms({
    required this.holdAmount,
    required this.fee,
    required this.refund,
    required this.isFree,
    required this.freeUntil,
    required this.slotBlocked,
  });

  factory CancellationTerms.fromJson(Map<String, dynamic> json) => CancellationTerms(
        holdAmount: _money(json['holdAmount']),
        fee: _money(json['fee']),
        refund: _money(json['refund']),
        isFree: json['isFree'] as bool,
        freeUntil: _date(json['freeUntil']),
        slotBlocked: json['slotBlocked'] as bool? ?? false,
      );

  final double holdAmount;
  final double fee;
  final double refund;
  final bool isFree;

  /// After this moment cancelling starts costing something.
  final DateTime freeUntil;

  /// Free because the space was still occupied, not because there is time in hand. The renter
  /// needs the difference: one is good timing, the other is the platform failing to deliver the
  /// thing they paid for, and the sentence that goes on the button is not the same.
  final bool slotBlocked;
}

/// One stored notification. The socket and the push are how a message arrives quickly; this row
/// is how it arrives at all — a phone in a basement misses the live update and nothing else.
class AppNotification {
  const AppNotification({
    required this.id,
    required this.kind,
    required this.title,
    required this.body,
    required this.isRead,
    required this.createdAt,
    this.subjectId,
  });

  factory AppNotification.fromJson(Map<String, dynamic> json) => AppNotification(
        id: json['id'] as String,
        kind: json['kind'] as String,
        title: json['title'] as String,
        body: json['body'] as String,
        subjectId: json['subjectId'] as String?,
        isRead: json['isRead'] as bool,
        createdAt: _date(json['createdAt']),
      );

  final String id;
  final String kind;
  final String title;
  final String body;

  /// What the notification is about — a booking, for everything the app currently sends. Null
  /// when there is nothing to open, in which case the row is not tappable.
  final String? subjectId;

  final bool isRead;
  final DateTime createdAt;
}

/// Whether this booking is still waiting on the caller's rating, and who it would be about.
///
/// Asked rather than worked out on the handset: "finished, not yet rated by you, and you were a
/// party to it" is three rules that live on the server, and a second copy here would eventually
/// offer a form the API then refuses.
class RatingPrompt {
  const RatingPrompt({
    required this.bookingId,
    required this.canRate,
    this.aboutUserId,
    this.reason,
  });

  factory RatingPrompt.fromJson(Map<String, dynamic> json) => RatingPrompt(
        bookingId: json['bookingId'] as String,
        canRate: json['canRate'] as bool,
        aboutUserId: json['aboutUserId'] as String?,
        reason: json['reason'] as String?,
      );

  final String bookingId;
  final bool canRate;
  final String? aboutUserId;

  /// Why not, when [canRate] is false. Worth showing for "you have already rated this" and worth
  /// swallowing for the rest.
  final String? reason;
}

class Rating {
  const Rating({
    required this.id,
    required this.bookingId,
    required this.toUserId,
    required this.score,
    required this.createdAt,
    this.comment,
  });

  factory Rating.fromJson(Map<String, dynamic> json) => Rating(
        id: json['id'] as String,
        bookingId: json['bookingId'] as String,
        toUserId: json['toUserId'] as String,
        score: json['score'] as int,
        comment: json['comment'] as String?,
        createdAt: _date(json['createdAt']),
      );

  final String id;
  final String bookingId;
  final String toUserId;
  final int score;
  final String? comment;
  final DateTime createdAt;
}

/// What a user's counterparties have said about them.
class Reputation {
  const Reputation({
    required this.userId,
    required this.trustScore,
    required this.ratingCount,
    required this.recent,
    this.averageScore,
  });

  factory Reputation.fromJson(Map<String, dynamic> json) => Reputation(
        userId: json['userId'] as String,
        trustScore: json['trustScore'] as int,
        averageScore: (json['averageScore'] as num?)?.toDouble(),
        ratingCount: json['ratingCount'] as int,
        recent: (json['recent'] as List<dynamic>)
            .map((e) => Rating.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  final String userId;

  /// The 0-100 signal the platform acts on, which is not the star average: it starts from a
  /// presumption of good faith and moves on conduct, where the average only reports opinions.
  final int trustScore;

  /// Null for someone nobody has rated. Deliberately not zero — "0.0 stars" against a new host
  /// reads as terrible, which is the opposite of what an absence of ratings means.
  final double? averageScore;

  final int ratingCount;
  final List<Rating> recent;
}

/// The secret behind the sticker at a space. Host only, and the reason the app draws the QR
/// itself rather than fetching an image: a token in a URL ends up in caches and logs.
class CheckInCode {
  const CheckInCode({required this.spaceId, required this.token});

  factory CheckInCode.fromJson(Map<String, dynamic> json) => CheckInCode(
        spaceId: json['spaceId'] as String,
        token: json['token'] as String,
      );

  final String spaceId;
  final String token;
}

/// A photograph on a listing.
class ListingPhoto {
  const ListingPhoto({required this.id, required this.url, required this.sortOrder});

  factory ListingPhoto.fromJson(Map<String, dynamic> json) => ListingPhoto(
        id: json['id'] as String,
        url: json['url'] as String,
        sortOrder: json['sortOrder'] as int,
      );

  final String id;

  /// Relative to the API host — the server decides where photos are served from, and a client
  /// that assembled the path itself would break the day storage moves off local disk.
  final String url;

  final int sortOrder;
}

/// One attempt at proving who you are.
class KycSubmission {
  const KycSubmission({
    required this.id,
    required this.legalName,
    required this.documentType,
    required this.documentLast4,
    required this.status,
    required this.submittedAt,
    this.documentPhotoUrl,
    this.payoutAccountLast4,
    this.reviewedAt,
    this.rejectionReason,
  });

  factory KycSubmission.fromJson(Map<String, dynamic> json) => KycSubmission(
        id: json['id'] as String,
        legalName: json['legalName'] as String,
        documentType: json['documentType'] as String,
        documentLast4: json['documentLast4'] as String,
        documentPhotoUrl: json['documentPhotoUrl'] as String?,
        payoutAccountLast4: json['payoutAccountLast4'] as String?,
        status: json['status'] as String,
        submittedAt: _date(json['submittedAt']),
        reviewedAt: _dateOrNull(json['reviewedAt']),
        rejectionReason: json['rejectionReason'] as String?,
      );

  final String id;
  final String legalName;
  final String documentType;

  /// The last four characters of the number. The whole of it never leaves the handset.
  final String documentLast4;

  final String? documentPhotoUrl;
  final String? payoutAccountLast4;
  final String status;
  final DateTime submittedAt;
  final DateTime? reviewedAt;

  /// Why it was refused, when it was. The one thing a rejected host actually needs.
  final String? rejectionReason;
}

/// Where the signed-in host stands with identity verification.
class KycState {
  const KycState({required this.status, required this.canCashOut, this.latest});

  factory KycState.fromJson(Map<String, dynamic> json) => KycState(
        status: json['status'] as String,
        canCashOut: json['canCashOut'] as bool,
        latest: json['latest'] == null
            ? null
            : KycSubmission.fromJson(json['latest'] as Map<String, dynamic>),
      );

  final String status;

  /// Answered by the server rather than inferred from [status], because cash-out is its rule.
  final bool canCashOut;

  final KycSubmission? latest;

  bool get isPending => status == 'Pending';
  bool get isVerified => status == 'Verified';
  bool get isRejected => status == 'Rejected';
}


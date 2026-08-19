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

  /// What the selected gateway needs to open its checkout sheet: SDK options for Razorpay, a path
  /// for the sandbox. An envelope, never a bare URL — see [checkoutUrl].
  final String checkoutPayload;

  /// The gateway's hosted page, or null when this gateway expects its own SDK instead.
  ///
  /// [apiBaseUrl] is needed because the gateway returns a path relative to its own host, which for
  /// the sandbox is the API itself. Treating [checkoutPayload] as a link directly is the obvious
  /// mistake and fails at launch with nothing useful to show the user.
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

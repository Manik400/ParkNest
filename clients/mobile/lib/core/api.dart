import 'api_client.dart';
import 'models.dart';
import 'session.dart';

/// One place that knows the API's shape, so a route change is a single edit.
///
/// Note what is absent from every signature below: a user id. The acting user comes from the
/// bearer token, server-side — the app cannot name someone else even by accident.
class Api {
  Api(this.client, this.session);

  final ApiClient client;
  final Session session;

  /// Run before the session is cleared, while the tokens are still valid.
  ///
  /// Exists for one caller: unregistering this handset from push. That call is authorised, so it
  /// has to happen before [Session.clear], and a callback keeps Api from depending on PushService
  /// — which depends on Api.
  Future<void> Function()? beforeSignOut;

  List<T> _list<T>(dynamic data, T Function(Map<String, dynamic>) parse) =>
      (data as List<dynamic>).map((e) => parse(e as Map<String, dynamic>)).toList();

  Map<String, dynamic> _map(dynamic data) => data as Map<String, dynamic>;

  // --- Auth ---------------------------------------------------------------

  /// Only an email address contains '@'. The API validates and normalises either one.
  Map<String, dynamic> _destination(String destination) {
    final value = destination.trim();
    return value.contains('@') ? {'email': value} : {'phone': value};
  }

  /// Sends a code to an email address or a phone number — whichever was typed.
  Future<OtpChallenge> requestOtp(String destination) async => OtpChallenge.fromJson(
        _map(await client.postAnonymous('/api/auth/request-otp', body: _destination(destination))),
      );

  Future<AuthResult> verifyOtp(String destination, String code) async {
    final result = AuthResult.fromJson(
      _map(await client.postAnonymous('/api/auth/verify-otp',
          body: {..._destination(destination), 'code': code})),
    );

    await session.save(result);
    return result;
  }

  /// Ends the session on the server, not just on the handset. A refresh token that outlives the
  /// sign-out is exactly the thing refresh tokens were added to make revocable.
  Future<void> signOut() async {
    await beforeSignOut?.call();

    final refreshToken = session.refreshToken;

    if (refreshToken != null) {
      try {
        await client.postAnonymous('/api/auth/logout', body: {'refreshToken': refreshToken});
      } on ApiException {
        // The local session goes regardless. A failed call here means the token expires on its
        // own schedule instead of immediately, which is not worth blocking the user over.
      }
    }

    await session.clear();
  }

  // --- Wallet -------------------------------------------------------------

  Future<Wallet> myWallet() async => Wallet.fromJson(_map(await client.get('/api/wallets/me')));

  Future<List<LedgerEntrySummary>> myTransactions({int limit = 50, int offset = 0}) async => _list(
        await client.get('/api/wallets/me/transactions',
            query: {'limit': limit, 'offset': offset}),
        LedgerEntrySummary.fromJson,
      );

  Future<void> cashOut(double amount, String idempotencyKey) => client.post(
        '/api/wallets/me/cash-out',
        body: {'amount': amount, 'idempotencyKey': idempotencyKey},
      );

  // --- Payments -----------------------------------------------------------

  /// Starts a credit purchase. Issues nothing — the wallet only moves when the gateway's signed
  /// webhook confirms the money arrived.
  ///
  /// `returnTo: 'app'` makes the browser's trip end on a "return to the app" page rather than the
  /// admin site's wallet, which a phone cannot reach; the app learns the outcome by polling.
  Future<StartPaymentResult> startPayment(double amount) async => StartPaymentResult.fromJson(
        _map(await client.post('/api/payments/orders', body: {'amount': amount, 'returnTo': 'app'})),
      );

  /// Where an order stands. Polled after checkout rather than trusting the return trip — coming
  /// back from the gateway proves the user pressed a button, not that the money arrived.
  Future<PaymentOrderView> paymentOrder(String orderId) async =>
      PaymentOrderView.fromJson(_map(await client.get('/api/payments/orders/$orderId')));

  // --- Vehicles -----------------------------------------------------------

  Future<List<Vehicle>> myVehicles() async =>
      _list(await client.get('/api/vehicles/me'), Vehicle.fromJson);

  Future<Vehicle> addVehicle(String plateNumber, String type) async => Vehicle.fromJson(
        _map(await client.post('/api/vehicles', body: {'plateNumber': plateNumber, 'type': type})),
      );

  Future<void> removeVehicle(String vehicleId) => client.delete('/api/vehicles/$vehicleId');

  // --- Listings and search ------------------------------------------------

  Future<List<NearbySpace>> searchNearby({
    required double latitude,
    required double longitude,
    int radiusMetres = 2000,
    double? maxPricePerHour,
  }) async =>
      _list(
        await client.get('/api/listings/nearby', query: {
          'lat': latitude,
          'lng': longitude,
          'radiusMetres': radiusMetres,
          if (maxPricePerHour != null) 'maxPricePerHour': maxPricePerHour,
        }),
        NearbySpace.fromJson,
      );

  Future<List<ListingSummary>> myListings() async =>
      _list(await client.get('/api/listings/me'), ListingSummary.fromJson);

  /// Creates a listing as a draft. Nothing is bookable until it is published, which is a separate
  /// call because publishing is what validates the price against the city band.
  Future<String> createListing(CreateListingRequest request) async {
    final response = _map(await client.post('/api/listings', body: request.toJson()));
    return response['id'] as String;
  }

  /// Puts the space on the map. Refused if the price falls outside the city band, or if the
  /// listing has no availability — an always-closed pin is worse than no pin.
  Future<void> publishListing(String spaceId) =>
      client.post('/api/listings/$spaceId/publish', body: {});

  Future<void> setListingStatus(String spaceId, String status) =>
      client.post('/api/listings/$spaceId/status', body: {'status': status});

  // --- Bookings -----------------------------------------------------------

  /// Prices a session without reserving anything, so the cost — and the reason it would be
  /// refused — can be shown before the renter commits.
  Future<BookingQuote> quote({
    required String spaceId,
    required DateTime startTime,
    required int durationMinutes,
  }) async =>
      BookingQuote.fromJson(_map(await client.get('/api/bookings/quote', query: {
        'spaceId': spaceId,
        'startTime': startTime.toUtc().toIso8601String(),
        'durationMinutes': durationMinutes,
      })));

  Future<String> book({
    required String parkingSpaceId,
    required String vehicleId,
    required DateTime startTime,
    required int durationMinutes,
    required String idempotencyKey,
  }) async {
    final response = _map(await client.post('/api/bookings', body: {
      'parkingSpaceId': parkingSpaceId,
      'vehicleId': vehicleId,
      'startTime': startTime.toUtc().toIso8601String(),
      'durationMinutes': durationMinutes,
      'idempotencyKey': idempotencyKey,
    }));

    return response['id'] as String;
  }

  Future<List<BookingSummary>> myBookings({String? status}) async => _list(
        await client.get('/api/bookings/me', query: {if (status != null) 'status': status}),
        BookingSummary.fromJson,
      );

  Future<List<BookingSummary>> hostingBookings({String? status}) async => _list(
        await client.get('/api/bookings/hosting', query: {if (status != null) 'status': status}),
        BookingSummary.fromJson,
      );

  Future<BookingDetail> booking(String bookingId) async =>
      BookingDetail.fromJson(_map(await client.get('/api/bookings/$bookingId')));

  /// Tier 1 check-in: the renter's word, recorded as exactly that.
  ///
  /// [proof] upgrades it to Tier 2 — the code off the sticker and where the phone was when it
  /// read it. The server decides whether that stands up; the app never claims a detection method
  /// it cannot back, because the method is the audit field a human reads when the two parties
  /// disagree about whether anyone was ever there.
  Future<void> startSession(String bookingId, {CheckInProof? proof}) =>
      client.post('/api/bookings/$bookingId/start', body: _sessionEvent(proof));

  Future<SessionOutcome> endSession(String bookingId, {CheckInProof? proof}) async =>
      SessionOutcome.fromJson(
        _map(await client.post('/api/bookings/$bookingId/end', body: _sessionEvent(proof))),
      );

  Map<String, dynamic> _sessionEvent(CheckInProof? proof) => proof == null
      ? {'method': 'AppConfirmed'}
      : {
          'method': 'QrGeofence',
          'qrToken': proof.token,
          'latitude': proof.latitude,
          'longitude': proof.longitude,
        };

  /// What cancelling would cost, without cancelling.
  Future<CancellationTerms> cancellationTerms(String bookingId) async =>
      CancellationTerms.fromJson(_map(await client.get('/api/bookings/$bookingId/cancellation')));

  Future<void> cancelBooking(String bookingId) =>
      client.post('/api/bookings/$bookingId/cancel', body: {});

  // --- Disputes -----------------------------------------------------------

  Future<List<Dispute>> myDisputes({bool onlyOpen = false}) async => _list(
        await client.get('/api/disputes/me', query: {'onlyOpen': onlyOpen}),
        Dispute.fromJson,
      );

  Future<Dispute> raiseDispute(String bookingId, String reason) async => Dispute.fromJson(
        _map(await client.post('/api/disputes', body: {'bookingId': bookingId, 'reason': reason})),
      );

  /// Attaches a photograph to a dispute — the blocked bay, the damage, the space that was never
  /// used. Only while the dispute is still undecided; the server enforces that.
  Future<Dispute> addDisputeEvidence(String disputeId, String filePath, {String? note}) async =>
      Dispute.fromJson(_map(await client.upload(
        '/api/disputes/$disputeId/evidence',
        filePath,
        fields: {if (note != null && note.isNotEmpty) 'note': note},
      )));

  // --- Listing photos -----------------------------------------------------

  Future<List<ListingPhoto>> listingPhotos(String spaceId) async =>
      _list(await client.get('/api/listings/$spaceId/photos'), ListingPhoto.fromJson);

  /// Uploads one photo from a file on the device. The server sniffs the bytes to decide what it
  /// is, so nothing here has to claim a content type it cannot vouch for.
  Future<ListingPhoto> addListingPhoto(String spaceId, String filePath) async =>
      ListingPhoto.fromJson(
        _map(await client.upload('/api/listings/$spaceId/photos', filePath)),
      );

  Future<void> removeListingPhoto(String spaceId, String photoId) =>
      client.delete('/api/listings/$spaceId/photos/$photoId');

  // --- Identity verification ----------------------------------------------

  Future<KycState> kycState() async => KycState.fromJson(_map(await client.get('/api/kyc/me')));

  /// Uploads the photograph first and returns where it was stored, because the document number
  /// travels in the JSON body that follows rather than as a form field a proxy might log.
  Future<String> uploadKycDocument(String filePath) async {
    final response = _map(await client.upload('/api/kyc/document', filePath));
    return response['url'] as String;
  }

  Future<KycSubmission> submitKyc({
    required String legalName,
    required String documentType,
    required String documentNumber,
    String? documentPhotoUrl,
    String? payoutAccountNumber,
  }) async =>
      KycSubmission.fromJson(_map(await client.post('/api/kyc', body: {
        'legalName': legalName,
        'documentType': documentType,
        'documentNumber': documentNumber,
        if (documentPhotoUrl != null) 'documentPhotoUrl': documentPhotoUrl,
        if (payoutAccountNumber != null && payoutAccountNumber.isNotEmpty)
          'payoutAccountNumber': payoutAccountNumber,
      })));

  // --- Check-in codes (host side) -----------------------------------------

  /// The code to print on the sticker, minted on first ask. Host only — a code any renter could
  /// fetch would prove nothing about them having stood anywhere.
  Future<CheckInCode> checkInCode(String spaceId) async =>
      CheckInCode.fromJson(_map(await client.get('/api/listings/$spaceId/check-in-code')));

  /// Issues a new code and retires the old one, for a sticker that has been photographed.
  Future<CheckInCode> rotateCheckInCode(String spaceId) async => CheckInCode.fromJson(
        _map(await client.post('/api/listings/$spaceId/check-in-code/rotate', body: {})),
      );

  // --- Ratings ------------------------------------------------------------

  Future<RatingPrompt> ratingPrompt(String bookingId) async =>
      RatingPrompt.fromJson(_map(await client.get('/api/ratings/prompt/$bookingId')));

  Future<Rating> rate(String bookingId, int score, {String? comment}) async => Rating.fromJson(
        _map(await client.post('/api/ratings', body: {
          'bookingId': bookingId,
          'score': score,
          if (comment != null && comment.isNotEmpty) 'comment': comment,
        })),
      );

  Future<Reputation> reputation(String userId) async =>
      Reputation.fromJson(_map(await client.get('/api/ratings/users/$userId')));

  // --- Notifications ------------------------------------------------------

  Future<List<AppNotification>> myNotifications({bool onlyUnread = false, int limit = 30}) async =>
      _list(
        await client.get('/api/notifications/me',
            query: {'onlyUnread': onlyUnread, 'limit': limit}),
        AppNotification.fromJson,
      );

  /// Just the number, for the badge. Cheaper than fetching a list to count it.
  Future<int> unreadNotificationCount() async =>
      (await client.get('/api/notifications/me/unread-count') as num).toInt();

  Future<void> markNotificationRead(String notificationId) =>
      client.post('/api/notifications/$notificationId/read', body: {});

  Future<void> markAllNotificationsRead() =>
      client.post('/api/notifications/me/read-all', body: {});

  // --- Devices ------------------------------------------------------------

  /// Points this install at the signed-in account. Called on every launch, because the platform
  /// rotates registration tokens on its own schedule.
  Future<void> registerDevice(String token, String platform) =>
      client.post('/api/devices', body: {'token': token, 'platform': platform});

  /// Called on sign-out, while the session is still valid. A handset left registered keeps
  /// receiving the previous account's bookings.
  Future<void> unregisterDevice(String token) =>
      client.post('/api/devices/unregister', body: {'token': token});
}

/// What a Tier 2 check-in offers as evidence: the code from the sticker, and where the phone was
/// when it read it. Either alone is weak — a photographed sticker travels, a GPS fix can be
/// spoofed — so the API takes them together or not at all.
class CheckInProof {
  const CheckInProof({required this.token, required this.latitude, required this.longitude});

  final String token;
  final double latitude;
  final double longitude;
}

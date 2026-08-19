import 'package:flutter_test/flutter_test.dart';
import 'package:parknest_mobile/core/models.dart';

/// Parsing is where a contract change bites first, and silently — a renamed field yields a null
/// rather than an error, and the screen shows a plausible-looking zero. These tests are pinned to
/// the exact JSON the API produces so that stays loud.
void main() {
  group('AuthResult', () {
    test('reads the whole token pair', () {
      final result = AuthResult.fromJson({
        'accessToken': 'header.payload.signature',
        'expiresAt': '2026-08-19T12:00:00+00:00',
        'userId': '11111111-1111-1111-1111-111111111111',
        'role': 'Both',
        'isNewUser': true,
        'refreshToken': 'a-long-random-string',
        'refreshExpiresAt': '2026-09-18T11:00:00+00:00',
      });

      expect(result.accessToken, 'header.payload.signature');
      expect(result.refreshToken, 'a-long-random-string');
      expect(result.isNewUser, isTrue);
      expect(
        result.refreshExpiresAt.isAfter(result.expiresAt),
        isTrue,
        reason: 'the refresh token must outlive the access token it replaces',
      );
    });
  });

  group('Wallet', () {
    test('keeps the three buckets apart', () {
      final wallet = Wallet.fromJson({
        'id': '22222222-2222-2222-2222-222222222222',
        'userId': '11111111-1111-1111-1111-111111111111',
        'spendable': 750.50,
        'held': 60,
        'earning': 1200.25,
      });

      expect(wallet.spendable, 750.50);
      expect(wallet.held, 60);
      expect(wallet.earning, 1200.25);
    });
  });

  group('LedgerEntrySummary', () {
    test('direction decides the sign shown to the user', () {
      Map<String, dynamic> entry(String direction) => {
            'transactionId': '33333333-3333-3333-3333-333333333333',
            'transactionType': 'Recharge',
            'account': 'Spendable',
            'direction': direction,
            'amount': 500,
            'bookingId': null,
            'description': 'Bought credits',
            'createdAt': '2026-08-19T10:00:00+00:00',
          };

      expect(LedgerEntrySummary.fromJson(entry('Credit')).isCredit, isTrue);
      expect(LedgerEntrySummary.fromJson(entry('Debit')).isCredit, isFalse);
    });
  });

  group('BookingSummary', () {
    Map<String, dynamic> booking(String status) => {
          'id': '44444444-4444-4444-4444-444444444444',
          'parkingSpaceId': '55555555-5555-5555-5555-555555555555',
          'spaceTitle': 'Driveway off 5th Cross',
          'spaceAddress': 'Indiranagar',
          'startTime': '2026-08-19T09:00:00+00:00',
          'expectedEndTime': '2026-08-19T10:00:00+00:00',
          'actualEndTime': null,
          'ratePerHour': 60,
          'holdAmount': 60,
          'settledAmount': 0,
          'status': status,
        };

    test('Held and Active are the states worth acting on', () {
      expect(BookingSummary.fromJson(booking('Held')).isOpen, isTrue);
      expect(BookingSummary.fromJson(booking('Active')).isOpen, isTrue);
      expect(BookingSummary.fromJson(booking('Completed')).isOpen, isFalse);
      expect(BookingSummary.fromJson(booking('Cancelled')).isOpen, isFalse);

      // A violation is finished as a session but is the opposite of fine. It must not be treated
      // as open — the renter cannot act on it — but it must not read as an ordinary completion
      // either, which is what the status chip colour is for.
      expect(BookingSummary.fromJson(booking('InViolation')).isOpen, isFalse);
    });

    test('a missing end time stays null rather than becoming an epoch', () {
      expect(BookingSummary.fromJson(booking('Held')).actualEndTime, isNull);
    });
  });

  group('PaymentOrderView', () {
    test('every terminal status counts as settled, so polling stops', () {
      PaymentOrderView order(String status) => PaymentOrderView.fromJson({
            'orderId': '66666666-6666-6666-6666-666666666666',
            'providerOrderId': 'order_abc',
            'amount': 500,
            'currency': 'INR',
            'status': status,
            'failureReason': null,
            'createdAt': '2026-08-19T10:00:00+00:00',
            'completedAt': null,
          });

      expect(order('Created').isSettled, isFalse);
      expect(order('Paid').isSettled, isTrue);
      expect(order('Failed').isSettled, isTrue);

      // Cancelled is the expiry sweep giving up. The app must stop waiting on it too, or the
      // wallet page would poll for three minutes over an order nothing will ever complete.
      expect(order('Cancelled').isSettled, isTrue);
    });
  });

  group('Dispute', () {
    Map<String, dynamic> dispute({String status = 'Open', num? adjustment}) => {
          'disputeId': '77777777-7777-7777-7777-777777777777',
          'bookingId': '44444444-4444-4444-4444-444444444444',
          'raisedByUserId': '11111111-1111-1111-1111-111111111111',
          'renterId': '11111111-1111-1111-1111-111111111111',
          'hostId': '88888888-8888-8888-8888-888888888888',
          'reason': 'The space was blocked.',
          'status': status,
          'resolution': null,
          'adjustmentAmount': adjustment,
          'adjustmentTransactionId': null,
          'evidence': <dynamic>[],
          'createdAt': '2026-08-19T10:00:00+00:00',
          'resolvedAt': null,
        };

    test('an upheld dispute with no payment has a null adjustment, not zero', () {
      // The difference matters on screen: null hides the refund line entirely, whereas zero would
      // tell the user "₹0.00 returned to your balance", which reads like a failure.
      expect(Dispute.fromJson(dispute(status: 'Resolved')).adjustmentAmount, isNull);
      expect(Dispute.fromJson(dispute(status: 'Resolved', adjustment: 60)).adjustmentAmount, 60);
    });

    test('both decided states end the dispute', () {
      expect(Dispute.fromJson(dispute(status: 'Open')).isDecided, isFalse);
      expect(Dispute.fromJson(dispute(status: 'UnderReview')).isDecided, isFalse);
      expect(Dispute.fromJson(dispute(status: 'Resolved')).isDecided, isTrue);
      expect(Dispute.fromJson(dispute(status: 'Rejected')).isDecided, isTrue);
    });
  });

  group('StartPaymentResult', () {
    test('carries an envelope, not a URL', () {
      // Found the hard way against a live sandbox: checkoutPayload is whatever the selected
      // gateway needs to open its sheet — a path for the sandbox, SDK options for Razorpay.
      // Handing it straight to a URL parser produces a launch failure with no useful message.
      final result = StartPaymentResult.fromJson({
        'orderId': '99999999-9999-9999-9999-999999999999',
        'providerOrderId': 'sbx_order_811b7ec976c1fe53',
        'amount': 500,
        'checkoutPayload': '{"provider":"Sandbox",'
            '"order_id":"sbx_order_811b7ec976c1fe53",'
            '"checkout_url":"/sandbox/checkout/sbx_order_811b7ec976c1fe53",'
            '"amount":500,"currency":"INR"}',
      });

      expect(result.providerOrderId, 'sbx_order_811b7ec976c1fe53');
      expect(
        result.checkoutUrl('http://10.0.2.2:5109'),
        'http://10.0.2.2:5109/sandbox/checkout/sbx_order_811b7ec976c1fe53',
        reason: 'the path is relative to the gateway host, which for the sandbox is the API',
      );
    });

    test('an absolute checkout url is left alone', () {
      final result = StartPaymentResult.fromJson({
        'orderId': '99999999-9999-9999-9999-999999999999',
        'providerOrderId': 'order_abc',
        'amount': 500,
        'checkoutPayload': '{"checkout_url":"https://checkout.example.test/pay/abc"}',
      });

      expect(result.checkoutUrl('http://10.0.2.2:5109'), 'https://checkout.example.test/pay/abc');
    });

    test('a gateway with no hosted page yields null rather than a broken link', () {
      // Razorpay expects its SDK to be opened with these options; there is nothing to launch.
      final result = StartPaymentResult.fromJson({
        'orderId': '99999999-9999-9999-9999-999999999999',
        'providerOrderId': 'order_abc',
        'amount': 500,
        'checkoutPayload': '{"key":"rzp_test_abc","order_id":"order_abc","amount":50000}',
      });

      expect(result.checkoutUrl('http://10.0.2.2:5109'), isNull);
    });
  });


  group('AvailabilityWindowRequest', () {
    test('equal start and end means the full twenty-four hours', () {
      // The API's convention, and the reason "always open" is not expressed by sending no windows
      // at all — no windows means never open, and publishing is refused.
      final window = const AvailabilityWindowRequest(
        dayOfWeek: 'Monday',
        startTime: Duration.zero,
        endTime: Duration.zero,
      ).toJson();

      expect(window['dayOfWeek'], 'Monday');
      expect(window['startTime'], '00:00:00');
      expect(window['endTime'], '00:00:00');
    });

    test('times serialise as the HH:mm:ss the API parses into a TimeOnly', () {
      final window = const AvailabilityWindowRequest(
        dayOfWeek: 'Friday',
        startTime: Duration(hours: 8, minutes: 30),
        endTime: Duration(hours: 20, minutes: 5),
      ).toJson();

      expect(window['startTime'], '08:30:00');
      expect(window['endTime'], '20:05:00');
    });
  });

  group('CreateListingRequest', () {
    test('sends every field the API needs to place and price the space', () {
      final json = const CreateListingRequest(
        title: 'Covered driveway',
        addressLine: 'Indiranagar',
        city: 'Bengaluru',
        latitude: 12.9716,
        longitude: 77.5946,
        pricePerHour: 60,
        supportedVehicleTypes: ['FourWheeler'],
        availabilityWindows: [],
      ).toJson();

      expect(json['city'], 'Bengaluru');
      expect(json['supportedVehicleTypes'], ['FourWheeler']);
      // Windows are wall-clock, so a space in another city would otherwise silently keep the
      // server's hours.
      expect(json['timeZoneId'], 'Asia/Kolkata');
    });
  });

}

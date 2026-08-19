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
}

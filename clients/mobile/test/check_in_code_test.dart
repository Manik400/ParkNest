import 'package:flutter_test/flutter_test.dart';
import 'package:parknest_mobile/core/check_in_code.dart';

/// The scanner reads whatever is in frame, and most of what it reads is not ours. What this file
/// pins down is which of those the app is willing to send to the check-in endpoint: a wrong token
/// is a round trip and a confusing rejection, where a locally recognised miss is immediate.
void main() {
  group('encode / parse', () {
    test('a code the host printed round-trips', () {
      const token = 'Xy7-2f_QlA9bmZ0pR4sT6uVw';

      expect(parseCheckInCode(encodeCheckInCode(token)), token);
    });

    test('the sticker carries a recognisable URI rather than a naked secret', () {
      expect(encodeCheckInCode('abc123'), 'parknest://check-in?token=abc123');
    });

    test('a token typed out by hand is still accepted', () {
      // Nothing stops a host printing the string itself, and refusing it would fail a scan that
      // would otherwise have worked.
      expect(parseCheckInCode('  abc123  '), 'abc123');
    });
  });

  group('codes that are not ours', () {
    test('another app\'s link is refused rather than forwarded as a token', () {
      expect(parseCheckInCode('https://example.com/menu'), isNull);
      expect(parseCheckInCode('WIFI:S:CafeGuest;T:WPA;P:hunter2;;'), isNull);
    });

    test('a parknest URI for something else is refused', () {
      expect(parseCheckInCode('parknest://pay?token=abc123'), isNull);
    });

    test('an empty or absent code yields nothing', () {
      expect(parseCheckInCode(null), isNull);
      expect(parseCheckInCode('   '), isNull);
      expect(parseCheckInCode('parknest://check-in'), isNull);
      expect(parseCheckInCode('parknest://check-in?token='), isNull);
    });
  });
}

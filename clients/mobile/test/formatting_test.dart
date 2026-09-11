import 'package:flutter_test/flutter_test.dart';
import 'package:parknest_mobile/core/formatting.dart';

void main() {
  group('formatDuration', () {
    test('reads as hours and minutes rather than raw minutes', () {
      expect(formatDuration(45), '45m');
      expect(formatDuration(60), '1h');
      expect(formatDuration(90), '1h 30m');
      expect(formatDuration(480), '8h');
    });
  });

  group('formatDistance', () {
    test('switches to kilometres where metres stop being readable', () {
      expect(formatDistance(250), '250 m');
      expect(formatDistance(999), '999 m');
      expect(formatDistance(1000), '1.0 km');
      expect(formatDistance(1400), '1.4 km');
    });
  });

  group('formatRemaining', () {
    final now = DateTime(2026, 8, 19, 10);

    test('counts down to the end of a session', () {
      expect(formatRemaining(now.add(const Duration(minutes: 30)), now: now), '30m left');
      expect(formatRemaining(now.add(const Duration(hours: 2)), now: now), '2h left');
    });

    test('says over, not a negative number, once the session runs past its end', () {
      // This is an overstay in progress and it is costing the renter money. "-25m left" would be
      // both ugly and easy to misread as time remaining.
      expect(formatRemaining(now.subtract(const Duration(minutes: 25)), now: now), '25m over');
    });
  });

  group('formatAgo', () {
    final now = DateTime(2026, 8, 19, 10);

    test('counts back in the unit the reader is thinking in', () {
      expect(formatAgo(now.subtract(const Duration(seconds: 20)), now: now), 'just now');
      expect(formatAgo(now.subtract(const Duration(minutes: 5)), now: now), '5m ago');
      expect(formatAgo(now.subtract(const Duration(hours: 3)), now: now), '3h ago');
      expect(formatAgo(now.subtract(const Duration(days: 2)), now: now), '2d ago');
    });

    test('gives the date back once the elapsed count stops being readable', () {
      // "23 days ago" is arithmetic the reader has to do to place the day.
      expect(formatAgo(DateTime(2026, 7, 27), now: now), '27 Jul 2026');
    });
  });
}

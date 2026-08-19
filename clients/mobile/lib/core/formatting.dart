import 'package:intl/intl.dart';

/// Credits are shown as rupees because that is what they are worth one-for-one, and calling them
/// anything else on a screen where someone is deciding whether to park would be false precision.
final NumberFormat _credits = NumberFormat.currency(locale: 'en_IN', symbol: '₹', decimalDigits: 2);

final DateFormat _dayTime = DateFormat('d MMM, h:mm a');
final DateFormat _timeOnly = DateFormat('h:mm a');

String formatCredits(double amount) => _credits.format(amount);

String formatDateTime(DateTime value) => _dayTime.format(value);

String formatTime(DateTime value) => _timeOnly.format(value);

/// "1h 30m", not "90 minutes" — the second is arithmetic the reader has to do.
String formatDuration(int minutes) {
  if (minutes < 60) {
    return '${minutes}m';
  }

  final hours = minutes ~/ 60;
  final rest = minutes % 60;

  return rest == 0 ? '${hours}h' : '${hours}h ${rest}m';
}

/// Distances read as metres up close and kilometres beyond that. "1400 m" is harder to place
/// than "1.4 km" when scanning a list.
String formatDistance(double metres) =>
    metres < 1000 ? '${metres.round()} m' : '${(metres / 1000).toStringAsFixed(1)} km';

/// How long until a moment, phrased for a countdown. Past times read as overdue, which on this
/// app means an overstay in progress and should never look neutral.
String formatRemaining(DateTime until, {DateTime? now}) {
  final difference = until.difference(now ?? DateTime.now());

  if (difference.isNegative) {
    return '${formatDuration(difference.abs().inMinutes)} over';
  }

  return '${formatDuration(difference.inMinutes)} left';
}

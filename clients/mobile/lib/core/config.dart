import 'dart:io' show Platform;

import 'package:flutter/foundation.dart';

/// Where the API lives.
///
/// The default is deliberately per-platform, because "localhost" means different things depending
/// on where the code is running: on an Android emulator it is the emulator itself, and the host
/// machine is reachable only at 10.0.2.2. Getting that wrong produces a connection timeout with
/// nothing in the API log to explain it, which is a bad first ten minutes for anyone new.
///
/// Override for a real device or a deployed API:
///
/// ```
/// flutter run --dart-define=PARKNEST_API_BASE_URL=http://192.168.1.20:5109
/// ```
class AppConfig {
  const AppConfig._();

  static const String _override = String.fromEnvironment('PARKNEST_API_BASE_URL');

  static String get apiBaseUrl {
    if (_override.isNotEmpty) {
      return _override;
    }

    // Plain HTTP on purpose in development. The API's https profile serves a self-signed
    // certificate, which a mobile client rejects outright — and teaching the app to accept bad
    // certificates is a habit that escapes into production. The http endpoint on 5109 is bound by
    // the same profile, so nothing is lost.
    if (kIsWeb) {
      return 'http://localhost:5109';
    }

    if (Platform.isAndroid) {
      return 'http://10.0.2.2:5109';
    }

    return 'http://localhost:5109';
  }

  /// How long to wait on a request before deciding the API is not there.
  static const Duration requestTimeout = Duration(seconds: 20);

  /// How often the wallet page re-checks a payment order after checkout.
  static const Duration paymentPollInterval = Duration(seconds: 2);

  /// Gives up polling rather than spinning forever if the webhook never lands.
  static const Duration paymentPollTimeout = Duration(minutes: 3);
}

import 'dart:async';
import 'dart:io';

import 'package:firebase_core/firebase_core.dart';
import 'package:firebase_messaging/firebase_messaging.dart';
import 'package:flutter/foundation.dart';

import 'api.dart';
import 'api_client.dart';

/// Handles a push that arrives while the app is in the background or terminated.
///
/// Must be a top-level function: the platform spins up a fresh isolate to run it, and an isolate
/// entry point cannot be a closure or an instance method. Nothing useful happens here on purpose —
/// the notification is already stored server-side and the tray entry is drawn by the system, so
/// re-fetching from a background isolate would duplicate work the app does on its next resume.
@pragma('vm:entry-point')
Future<void> handleBackgroundMessage(RemoteMessage message) async {}

/// Registers this install for push, and keeps the registration honest.
///
/// Every method here tolerates Firebase not being configured, because it usually is not: the repo
/// ships without google-services.json and the app is expected to run anyway. When initialisation
/// fails the service marks itself disabled and returns, and the rest of the app is unaffected —
/// notifications are stored on the server, listed in the app and counted on the bell regardless of
/// whether a push ever reaches the handset.
class PushService {
  PushService(this._api);

  final Api _api;

  final _messages = StreamController<RemoteMessage>.broadcast();

  StreamSubscription<String>? _tokenRefresh;
  StreamSubscription<RemoteMessage>? _foreground;

  String? _token;
  bool _enabled = false;
  bool _started = false;

  /// True once a token has been obtained and handed to the API.
  bool get isEnabled => _enabled;

  /// Pushes that arrived while the app was in the foreground.
  ///
  /// The system draws nothing for these — a foreground push is the app's problem — and the bell
  /// listens so its badge moves at the moment the message lands rather than on the next poll.
  Stream<RemoteMessage> get onMessage => _messages.stream;

  /// Called after sign-in, and safe to call again.
  ///
  /// Registration is repeated on every launch rather than done once, because the platform rotates
  /// a registration token whenever it likes — after a reinstall, a restore, a long silence — and a
  /// token registered once is a handset that quietly stops receiving anything.
  Future<void> start() async {
    if (_started) {
      return;
    }
    _started = true;

    try {
      await Firebase.initializeApp();
    } catch (error) {
      // The expected path in a checkout with no Firebase project. Reported once, at debug level,
      // because it is a configuration state rather than a fault.
      debugPrint('Push disabled: Firebase is not configured ($error).');
      _started = false;
      return;
    }

    final messaging = FirebaseMessaging.instance;

    try {
      // Asked for here rather than at launch. By this point the user has signed in, so the prompt
      // arrives attached to an account that has something to be notified about.
      final settings = await messaging.requestPermission();

      if (settings.authorizationStatus == AuthorizationStatus.denied) {
        // Refused, and that is a real answer. No token is fetched, so nothing is registered and
        // the server never tries to reach a handset that has said no.
        debugPrint('Push declined by the user.');
        _started = false;
        return;
      }

      FirebaseMessaging.onBackgroundMessage(handleBackgroundMessage);

      final token = await messaging.getToken();
      if (token == null) {
        _started = false;
        return;
      }

      await _register(token);

      _tokenRefresh = messaging.onTokenRefresh.listen(_register);
      _foreground = FirebaseMessaging.onMessage.listen(_messages.add);
    } catch (error) {
      debugPrint('Push registration failed: $error');
      _started = false;
    }
  }

  /// Called on sign-out, before the tokens are cleared.
  ///
  /// The unregister has to happen while the session is still valid — the endpoint is authorised,
  /// and a call made after [Session.clear] would be rejected. A handset that is not unregistered
  /// keeps receiving the previous account's bookings, which is the whole reason this exists.
  Future<void> stop() async {
    await _tokenRefresh?.cancel();
    await _foreground?.cancel();
    _tokenRefresh = null;
    _foreground = null;

    final token = _token;
    _token = null;
    _enabled = false;
    _started = false;

    if (token == null) {
      return;
    }

    try {
      await _api.unregisterDevice(token);
    } on ApiException {
      // Signing out must not fail because a best-effort call did. The server prunes a token
      // Firebase disowns anyway, so the worst case is a stale row until the next push to it.
    }
  }

  Future<void> _register(String token) async {
    try {
      await _api.registerDevice(token, _platform);
      _token = token;
      _enabled = true;
    } on ApiException catch (error) {
      debugPrint('Could not register this device for push: ${error.message}');
    }
  }

  /// What the API stores alongside the token. It only accepts these three.
  String get _platform {
    if (kIsWeb) {
      return 'web';
    }

    return Platform.isIOS ? 'ios' : 'android';
  }

  void dispose() {
    _tokenRefresh?.cancel();
    _foreground?.cancel();
    _messages.close();
  }
}

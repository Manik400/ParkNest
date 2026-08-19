import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import 'models.dart';

/// The signed-in session, and the only thing that knows where the tokens live.
///
/// Tokens go to the platform keystore/keychain rather than shared preferences. The refresh token
/// is a month-long session in a single string; on a rooted device plain preferences are readable
/// by anything on the handset.
///
/// A [ChangeNotifier] because exactly one thing needs to react to it — the router, which decides
/// between the app and the sign-in screen. Anything heavier would be machinery for one listener.
class Session extends ChangeNotifier {
  Session({FlutterSecureStorage? storage})
      : _storage = storage ??
            const FlutterSecureStorage(
              aOptions: AndroidOptions(encryptedSharedPreferences: true),
            );

  static const _accessKey = 'parknest.accessToken';
  static const _accessExpiryKey = 'parknest.accessExpiresAt';
  static const _refreshKey = 'parknest.refreshToken';
  static const _userKey = 'parknest.userId';
  static const _roleKey = 'parknest.role';

  final FlutterSecureStorage _storage;

  String? _accessToken;
  DateTime? _accessExpiresAt;
  String? _refreshToken;
  String? _userId;
  String? _role;

  bool _restored = false;

  /// False until [restore] has run. The router shows a splash rather than bouncing a signed-in
  /// user to the login screen for the frame it takes to read the keystore.
  bool get isRestored => _restored;

  String? get userId => _userId;
  String? get role => _role;

  /// True while there is any way back to a valid access token, including via refresh.
  bool get isSignedIn => _refreshToken != null;

  bool get isHost => _role == 'Host' || _role == 'Both' || _role == 'Admin';

  String? get refreshToken => _refreshToken;

  /// The access token, or null when it has expired. Expiry is checked here so a request that is
  /// certain to come back 401 is not sent at all.
  String? get accessToken {
    final expiry = _accessExpiresAt;

    if (_accessToken == null || expiry == null) {
      return null;
    }

    return expiry.isAfter(DateTime.now()) ? _accessToken : null;
  }

  Future<void> restore() async {
    _accessToken = await _storage.read(key: _accessKey);
    _refreshToken = await _storage.read(key: _refreshKey);
    _userId = await _storage.read(key: _userKey);
    _role = await _storage.read(key: _roleKey);

    final expiry = await _storage.read(key: _accessExpiryKey);
    _accessExpiresAt = expiry == null ? null : DateTime.tryParse(expiry);

    _restored = true;
    notifyListeners();
  }

  Future<void> save(AuthResult result) async {
    _accessToken = result.accessToken;
    _accessExpiresAt = result.expiresAt;
    _refreshToken = result.refreshToken;
    _userId = result.userId;
    _role = result.role;

    await Future.wait([
      _storage.write(key: _accessKey, value: result.accessToken),
      _storage.write(key: _accessExpiryKey, value: result.expiresAt.toIso8601String()),
      _storage.write(key: _refreshKey, value: result.refreshToken),
      _storage.write(key: _userKey, value: result.userId),
      _storage.write(key: _roleKey, value: result.role),
    ]);

    notifyListeners();
  }

  Future<void> clear() async {
    _accessToken = null;
    _accessExpiresAt = null;
    _refreshToken = null;
    _userId = null;
    _role = null;

    await _storage.deleteAll();

    notifyListeners();
  }
}

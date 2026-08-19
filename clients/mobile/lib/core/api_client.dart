import 'dart:async';

import 'package:dio/dio.dart';

import 'config.dart';
import 'models.dart';
import 'session.dart';

/// A failure the user can be shown.
///
/// The API answers errors as RFC 7807 problem details, and its `detail` is written for a person —
/// "Minimum cash-out is 500.00 credits", not a code. Surfacing that beats anything the client
/// could invent, so this carries it through and only falls back when there is nothing to carry.
class ApiException implements Exception {
  const ApiException(this.message, {this.statusCode, this.retryAfter});

  final String message;
  final int? statusCode;

  /// Present on a 429, from the header. The caller can tell the user when to come back.
  final Duration? retryAfter;

  bool get isUnauthorised => statusCode == 401;
  bool get isForbidden => statusCode == 403;

  /// The renter is short on credits. The API uses 402 for exactly this, so the app can offer to
  /// top up rather than showing a generic failure.
  bool get isInsufficientCredits => statusCode == 402;

  @override
  String toString() => message;
}

/// Wraps Dio with the two things every call needs: a bearer token, and the refresh-and-retry
/// dance when it has expired.
///
/// Access tokens last an hour, so a 401 is usually just age rather than a dead session. The
/// interceptor spends the refresh token, replays the request once, and only signs the user out
/// when that fails too — the difference between an hour-long session and a month-long one.
class ApiClient {
  ApiClient(this._session) {
    _dio = Dio(
      BaseOptions(
        baseUrl: AppConfig.apiBaseUrl,
        connectTimeout: AppConfig.requestTimeout,
        receiveTimeout: AppConfig.requestTimeout,
        // Non-2xx is handled here rather than thrown by Dio, so one place turns a status into an
        // ApiException and the call sites stay free of status checks.
        validateStatus: (_) => true,
        headers: {'Content-Type': 'application/json'},
      ),
    );
  }

  final Session _session;
  late final Dio _dio;

  /// The refresh in flight, if any. Shared, because a screen that fires three requests at once
  /// against an expired token would otherwise start three refreshes — and since every rotation
  /// invalidates the last, two of them would look like a replay and the API would end the session.
  /// The client would have logged itself out by trying too hard.
  Future<bool>? _refreshInFlight;

  /// Signals that the session ended and the user must sign in again. The app listens once, at the
  /// router, rather than every screen handling it.
  final StreamController<void> _signedOut = StreamController<void>.broadcast();

  Stream<void> get onSignedOut => _signedOut.stream;

  Future<dynamic> get(String path, {Map<String, dynamic>? query}) =>
      _send(() => _dio.get<dynamic>(path, queryParameters: query, options: _auth()));

  Future<dynamic> post(String path, {Object? body}) =>
      _send(() => _dio.post<dynamic>(path, data: body, options: _auth()));

  Future<dynamic> delete(String path) =>
      _send(() => _dio.delete<dynamic>(path, options: _auth()));

  /// Unauthenticated: sign-in, refresh and sign-out speak for themselves and must never trigger
  /// the refresh path, or a rejected code would loop.
  Future<dynamic> postAnonymous(String path, {Object? body}) async {
    final response = await _guard(() => _dio.post<dynamic>(path, data: body));
    return _unwrap(response);
  }

  Options _auth() {
    final token = _session.accessToken;
    return Options(headers: token == null ? null : {'Authorization': 'Bearer $token'});
  }

  Future<dynamic> _send(Future<Response<dynamic>> Function() request) async {
    var response = await _guard(request);

    if (response.statusCode != 401) {
      return _unwrap(response);
    }

    // Once only. A second 401 with a token minted seconds ago is the API saying no, not a timing
    // problem, and retrying again would loop.
    final refreshed = await _refresh();

    if (!refreshed) {
      await _session.clear();
      _signedOut.add(null);
      throw const ApiException('Your session has ended. Sign in again.', statusCode: 401);
    }

    response = await _guard(request);

    if (response.statusCode == 401) {
      await _session.clear();
      _signedOut.add(null);
    }

    return _unwrap(response);
  }

  Future<bool> _refresh() {
    final existing = _refreshInFlight;

    if (existing != null) {
      return existing;
    }

    final future = _doRefresh().whenComplete(() => _refreshInFlight = null);
    _refreshInFlight = future;

    return future;
  }

  Future<bool> _doRefresh() async {
    final refreshToken = _session.refreshToken;

    if (refreshToken == null) {
      return false;
    }

    try {
      final response = await _dio.post<dynamic>(
        '/api/auth/refresh',
        data: {'refreshToken': refreshToken},
      );

      if (response.statusCode != 200 || response.data is! Map) {
        return false;
      }

      // The rotated token *is* the session now. Failing to store it would strand the next request
      // and read to the API as a replay.
      await _session.save(AuthResult.fromJson(response.data as Map<String, dynamic>));

      return true;
    } on DioException {
      return false;
    }
  }

  /// Turns transport failures into something showable. A dead API is the single most common thing
  /// to hit in development, and "connection refused" helps nobody.
  Future<Response<dynamic>> _guard(Future<Response<dynamic>> Function() request) async {
    try {
      return await request();
    } on DioException catch (error) {
      throw ApiException(_describeTransport(error));
    }
  }

  static String _describeTransport(DioException error) {
    switch (error.type) {
      case DioExceptionType.connectionTimeout:
      case DioExceptionType.sendTimeout:
      case DioExceptionType.receiveTimeout:
        return 'The API did not respond in time.';
      case DioExceptionType.connectionError:
        return 'Could not reach the API at ${AppConfig.apiBaseUrl}. Is it running?';
      default:
        return 'Something went wrong talking to the API.';
    }
  }

  dynamic _unwrap(Response<dynamic> response) {
    final status = response.statusCode ?? 0;

    if (status >= 200 && status < 300) {
      return response.data;
    }

    throw ApiException(
      _describeProblem(response),
      statusCode: status,
      retryAfter: _retryAfter(response),
    );
  }

  static Duration? _retryAfter(Response<dynamic> response) {
    final header = response.headers.value('retry-after');
    final seconds = header == null ? null : int.tryParse(header);

    return seconds == null ? null : Duration(seconds: seconds);
  }

  static String _describeProblem(Response<dynamic> response) {
    final body = response.data;

    if (body is Map) {
      final detail = body['detail'] ?? body['title'];
      if (detail is String && detail.isNotEmpty) {
        return detail;
      }
    }

    switch (response.statusCode) {
      case 401:
        return 'Sign in to continue.';
      case 402:
        return 'Not enough credits.';
      case 403:
        return 'That is not yours to change.';
      case 429:
        return 'Too many requests. Try again shortly.';
      default:
        return 'Request failed (${response.statusCode}).';
    }
  }

  void dispose() => _signedOut.close();
}

/// How a check-in code travels between the host's sticker and the renter's camera.
///
/// The QR carries a URI rather than the bare token, for two reasons. A generic camera app shows
/// the user something recognisable instead of forty random characters, and the scanner can tell a
/// ParkNest sticker from the takeaway menu taped next to it — which turns "that code does not
/// belong to this space" from a round trip into an immediate, accurate message.
library;

const _scheme = 'parknest';
const _host = 'check-in';

/// What the host's sticker encodes.
String encodeCheckInCode(String token) =>
    Uri(scheme: _scheme, host: _host, queryParameters: {'token': token}).toString();

/// The token inside a scanned code, or null if this QR was never one of ours.
///
/// A bare token is accepted too: nothing stops a host from printing the string by hand, and
/// refusing it would fail a scan that would have worked.
String? parseCheckInCode(String? raw) {
  final value = raw?.trim();

  if (value == null || value.isEmpty) {
    return null;
  }

  final uri = Uri.tryParse(value);

  if (uri != null && uri.scheme == _scheme) {
    if (uri.host != _host) {
      return null;
    }

    final token = uri.queryParameters['token'];
    return token != null && token.isNotEmpty ? token : null;
  }

  // Anything with a scheme is some other app's link — a URL, a wifi config, a vCard — and
  // sending it as a token would only produce a confusing rejection from the server.
  return uri != null && uri.hasScheme ? null : value;
}

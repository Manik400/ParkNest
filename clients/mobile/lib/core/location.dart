import 'package:geolocator/geolocator.dart';

/// Why a location request did not produce a location.
///
/// Separate cases because the remedy differs and the user is the one who has to apply it: turning
/// on GPS is a different action from granting a permission, and "denied forever" can only be
/// undone in system settings. Collapsing them into a null would leave the app saying "could not
/// get your location" to someone who could fix it in two taps if only they were told which two.
enum LocationFailure {
  serviceDisabled,
  denied,
  deniedForever,
  failed,
}

class LocationResult {
  const LocationResult.success(this.latitude, this.longitude) : failure = null;
  const LocationResult.failed(this.failure) : latitude = null, longitude = null;

  final double? latitude;
  final double? longitude;
  final LocationFailure? failure;

  bool get isSuccess => failure == null;

  String get message => switch (failure) {
        LocationFailure.serviceDisabled => 'Location is switched off on this device.',
        LocationFailure.denied => 'ParkNest needs location access to find spaces near you.',
        LocationFailure.deniedForever =>
          'Location access is blocked. Turn it on for ParkNest in system settings.',
        LocationFailure.failed => 'Could not get a location fix.',
        null => '',
      };

  /// Whether the app can do anything about it, or whether the user has to go to settings.
  bool get isRecoverableInApp => failure == LocationFailure.denied;
}

/// Wraps geolocator so the permission ladder lives in one place rather than at every call site.
class LocationService {
  const LocationService();

  Future<LocationResult> current() async {
    if (!await Geolocator.isLocationServiceEnabled()) {
      return const LocationResult.failed(LocationFailure.serviceDisabled);
    }

    var permission = await Geolocator.checkPermission();

    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }

    if (permission == LocationPermission.deniedForever) {
      return const LocationResult.failed(LocationFailure.deniedForever);
    }

    if (permission == LocationPermission.denied) {
      return const LocationResult.failed(LocationFailure.denied);
    }

    try {
      // Medium accuracy on purpose. The search radius starts at a kilometre, so metres of
      // precision buy nothing and cost a noticeably longer fix and more battery.
      final position = await Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
          accuracy: LocationAccuracy.medium,
          timeLimit: Duration(seconds: 15),
        ),
      );

      return LocationResult.success(position.latitude, position.longitude);
    } on Exception {
      // A timeout indoors is ordinary rather than exceptional, and the caller has a usable
      // fallback, so this reports rather than throws.
      return const LocationResult.failed(LocationFailure.failed);
    }
  }

  Future<void> openSettings() => Geolocator.openAppSettings();
}

import 'package:flutter/material.dart';
import 'package:mobile_scanner/mobile_scanner.dart';

import '../core/check_in_code.dart';

/// Reads the sticker at the space and returns its token.
///
/// Returning the token rather than checking in from here is deliberate: the check-in also needs a
/// location fix, and the caller is the screen that knows which booking is being started and what
/// to do when the geofence refuses it. This screen's whole job is the camera.
class ScanCheckInScreen extends StatefulWidget {
  const ScanCheckInScreen({super.key});

  @override
  State<ScanCheckInScreen> createState() => _ScanCheckInScreenState();
}

class _ScanCheckInScreenState extends State<ScanCheckInScreen> {
  final MobileScannerController _controller = MobileScannerController(
    // QR only. Every other symbology in frame — a barcode on a parcel, the plate sticker — is a
    // detection that can only ever be rejected, and each one costs a frame of processing.
    formats: const [BarcodeFormat.qrCode],
    detectionSpeed: DetectionSpeed.noDuplicates,
  );

  /// Set once a code has been taken, because detections keep arriving while the route pops and
  /// a second `Navigator.pop` would take the screen underneath with it.
  bool _handled = false;

  /// Shown when the camera reads something that is not one of our stickers. Not fatal — the user
  /// simply keeps scanning — so it sits under the viewfinder rather than closing the screen.
  String? _wrongCode;

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  void _onDetect(BarcodeCapture capture) {
    if (_handled) return;

    for (final barcode in capture.barcodes) {
      final token = parseCheckInCode(barcode.rawValue);

      if (token != null) {
        _handled = true;
        Navigator.pop(context, token);
        return;
      }
    }

    if (mounted && _wrongCode == null) {
      setState(() => _wrongCode = 'That is not a ParkNest check-in code.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(
        title: const Text('Scan the code'),
        actions: [
          IconButton(
            onPressed: () => _controller.toggleTorch(),
            icon: const Icon(Icons.flashlight_on_outlined),
            tooltip: 'Torch',
          ),
        ],
      ),
      body: Column(
        children: [
          Expanded(
            child: Stack(
              fit: StackFit.expand,
              children: [
                MobileScanner(
                  controller: _controller,
                  onDetect: _onDetect,
                  // A basement bay is exactly where a camera fails, and the fallback — checking in
                  // by tapping — is one screen back. Saying so beats a black rectangle.
                  errorBuilder: (context, error, _) => Padding(
                    padding: const EdgeInsets.all(24),
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Icon(Icons.no_photography_outlined, size: 40, color: scheme.error),
                        const SizedBox(height: 12),
                        Text(
                          'The camera is not available: ${error.errorCode.name}.',
                          textAlign: TextAlign.center,
                        ),
                        const SizedBox(height: 8),
                        const Text(
                          'Go back and confirm in the app instead — it checks you in just the '
                          'same, and records that it was your word rather than a scan.',
                          textAlign: TextAlign.center,
                        ),
                      ],
                    ),
                  ),
                ),
                IgnorePointer(
                  child: Center(
                    child: Container(
                      width: 240,
                      height: 240,
                      decoration: BoxDecoration(
                        border: Border.all(color: Colors.white70, width: 3),
                        borderRadius: BorderRadius.circular(16),
                      ),
                    ),
                  ),
                ),
              ],
            ),
          ),
          Container(
            width: double.infinity,
            color: scheme.surfaceContainerHighest,
            padding: const EdgeInsets.fromLTRB(24, 16, 24, 32),
            child: Column(
              children: [
                Text(
                  _wrongCode ??
                      'Point the camera at the ParkNest sticker at the space. Your location is '
                          'checked too, so scanning a photo of it from home will not work.',
                  textAlign: TextAlign.center,
                  style: TextStyle(
                    color: _wrongCode == null ? scheme.onSurfaceVariant : scheme.error,
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

import 'package:flutter/material.dart';
import 'package:qr_flutter/qr_flutter.dart';

import '../core/api_client.dart';
import '../core/check_in_code.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// The code to print and stick up at the space.
///
/// Drawn on the handset rather than fetched as an image: the token is the whole secret behind a
/// Tier 2 check-in, and putting it in an image URL would leave copies in every cache and access
/// log between here and the server. It is minted on first ask, so a host who never opens this
/// screen simply has a space that takes Tier 1 check-ins.
class CheckInCodeScreen extends StatefulWidget {
  const CheckInCodeScreen({required this.spaceId, required this.title, super.key});

  final String spaceId;
  final String title;

  @override
  State<CheckInCodeScreen> createState() => _CheckInCodeScreenState();
}

class _CheckInCodeScreenState extends State<CheckInCodeScreen> {
  late Future<CheckInCode> _code;
  bool _busy = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    setState(() => _code = Services.of(context).checkInCode(widget.spaceId));
  }

  Future<void> _rotate() async {
    final api = Services.of(context);

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Print a new code?'),
        content: const Text(
          'The sticker at the space stops working the moment this is issued. Do it if the old '
          'code has been photographed or shared — renters will have to check in by tapping until '
          'the new one is up.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Keep it')),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Issue a new code'),
          ),
        ],
      ),
    );

    if (!(confirmed ?? false)) return;

    setState(() => _busy = true);

    try {
      final rotated = await api.rotateCheckInCode(widget.spaceId);
      if (mounted) setState(() => _code = Future.value(rotated));
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Check-in code')),
      body: FutureBuilder<CheckInCode>(
        future: _code,
        builder: (context, snapshot) => AsyncView<CheckInCode>(
          snapshot: snapshot,
          onRetry: _load,
          builder: _build,
        ),
      ),
    );
  }

  Widget _build(CheckInCode code) {
    final scheme = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        Text(widget.title, style: Theme.of(context).textTheme.titleLarge),
        const SizedBox(height: 16),
        Card(
          child: Padding(
            padding: const EdgeInsets.all(24),
            child: Center(
              child: QrImageView(
                data: encodeCheckInCode(code.token),
                size: 240,
                // Always on white with black modules, whatever the app's theme. A dark-mode QR
                // printed as-is is a code no camera reads.
                backgroundColor: Colors.white,
                eyeStyle: const QrEyeStyle(
                  eyeShape: QrEyeShape.square,
                  color: Colors.black,
                ),
                dataModuleStyle: const QrDataModuleStyle(
                  dataModuleShape: QrDataModuleShape.square,
                  color: Colors.black,
                ),
              ),
            ),
          ),
        ),
        const SizedBox(height: 16),
        SectionCard(
          title: 'Putting it up',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Print this and fix it where a driver can reach it from the bay — a wall, a post, '
                'the back of the gate. A renter who scans it and is standing within 150 m of the '
                'pin gets a check-in recorded as evidence rather than as their word, which is what '
                'settles an argument about whether they were ever here.',
                style: TextStyle(color: scheme.onSurfaceVariant),
              ),
              const SizedBox(height: 16),
              OutlinedButton.icon(
                onPressed: _busy ? null : _rotate,
                icon: const Icon(Icons.autorenew),
                label: const Text('Issue a new code'),
              ),
            ],
          ),
        ),
      ],
    );
  }
}

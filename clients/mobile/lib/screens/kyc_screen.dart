import 'package:flutter/material.dart';
import 'package:image_picker/image_picker.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// Proving who you are, which is what opens cash-out.
///
/// A host can earn credits from the first booking and cannot convert them into money until an
/// operator has read a document. That is the compliance posture the whole credit model rests on
/// (PRD §16) — credits are an accounting layer over an escrow arrangement, not a wallet we issue —
/// so this screen is deliberately explicit about what happens to what it collects.
class KycScreen extends StatefulWidget {
  const KycScreen({super.key});

  @override
  State<KycScreen> createState() => _KycScreenState();
}

class _KycScreenState extends State<KycScreen> {
  static const _documentTypes = [
    (value: 'Pan', label: 'PAN card'),
    (value: 'Aadhaar', label: 'Aadhaar'),
    (value: 'DrivingLicence', label: 'Driving licence'),
    (value: 'Passport', label: 'Passport'),
  ];

  late Future<KycState> _state;

  final TextEditingController _name = TextEditingController();
  final TextEditingController _number = TextEditingController();
  final TextEditingController _account = TextEditingController();

  String _documentType = 'Pan';
  XFile? _document;
  bool _busy = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  @override
  void dispose() {
    _name.dispose();
    _number.dispose();
    _account.dispose();
    super.dispose();
  }

  void _load() {
    setState(() => _state = Services.of(context).kycState());
  }

  Future<void> _pickDocument() async {
    final picked = await ImagePicker().pickImage(
      source: ImageSource.camera,
      // Downscaled before it leaves the handset. A modern phone camera produces something well
      // past the server's limit, and an operator reading a document number needs it legible,
      // not enormous.
      maxWidth: 1600,
      imageQuality: 85,
    );

    if (picked != null && mounted) setState(() => _document = picked);
  }

  Future<void> _submit() async {
    final api = Services.of(context);
    setState(() => _busy = true);

    try {
      // The photograph goes first and separately: the document number belongs in the JSON body
      // that follows, not in a multipart field that a proxy might write to a log.
      String? photoUrl;

      if (_document case final document?) {
        photoUrl = await api.uploadKycDocument(document.path);
      }

      await api.submitKyc(
        legalName: _name.text.trim(),
        documentType: _documentType,
        documentNumber: _number.text.trim(),
        documentPhotoUrl: photoUrl,
        payoutAccountNumber: _account.text.trim(),
      );

      if (!mounted) return;

      _number.clear();
      showMessage(context, 'Sent for review. You will be told either way.');
      _load();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Verify your identity')),
      body: FutureBuilder<KycState>(
        future: _state,
        builder: (context, snapshot) => AsyncView<KycState>(
          snapshot: snapshot,
          onRetry: _load,
          builder: _build,
        ),
      ),
    );
  }

  Widget _build(KycState state) {
    final scheme = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        _StatusCard(state: state),
        const SizedBox(height: 16),

        if (state.isVerified)
          SectionCard(
            title: 'What this unlocks',
            child: Text(
              'Cash-out is open. Earnings move to the bank account ending '
              '${state.latest?.payoutAccountLast4 ?? '—'} once an operator has made the transfer.',
              style: TextStyle(color: scheme.onSurfaceVariant),
            ),
          )
        else ...[
          SectionCard(
            title: state.isRejected ? 'Try again' : 'Your details',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                TextField(
                  controller: _name,
                  enabled: !_busy,
                  textCapitalization: TextCapitalization.words,
                  decoration: const InputDecoration(
                    labelText: 'Name, exactly as printed on the document',
                  ),
                ),
                const SizedBox(height: 12),
                DropdownButtonFormField<String>(
                  value: _documentType,
                  decoration: const InputDecoration(labelText: 'Document'),
                  items: [
                    for (final type in _documentTypes)
                      DropdownMenuItem(value: type.value, child: Text(type.label)),
                  ],
                  onChanged: _busy ? null : (value) => setState(() => _documentType = value!),
                ),
                const SizedBox(height: 12),
                TextField(
                  controller: _number,
                  enabled: !_busy,
                  textCapitalization: TextCapitalization.characters,
                  decoration: const InputDecoration(labelText: 'Document number'),
                ),
                const SizedBox(height: 6),
                Text(
                  // Said plainly, because it is unusual and it is the reason someone should be
                  // willing to type it in at all.
                  'Only the last four characters are stored. The rest is turned into a one-way '
                  'fingerprint used to spot the same document on two accounts, and then dropped.',
                  style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                ),
                const SizedBox(height: 12),
                TextField(
                  controller: _account,
                  enabled: !_busy,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(
                    labelText: 'Bank account for payouts (optional)',
                  ),
                ),
              ],
            ),
          ),
          const SizedBox(height: 16),

          SectionCard(
            title: 'Photograph of the document',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  _document == null
                      ? 'A number typed into a form proves only that somebody knows a number.'
                      : 'Ready to send: ${_document!.name}',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  onPressed: _busy ? null : _pickDocument,
                  icon: const Icon(Icons.photo_camera_outlined),
                  label: Text(_document == null ? 'Take a photo' : 'Retake'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 16),

          FilledButton(
            onPressed: _busy || _name.text.trim().isEmpty ? null : _submit,
            child: Text(_busy ? 'Sending…' : 'Send for review'),
          ),
        ],
      ],
    );
  }
}

class _StatusCard extends StatelessWidget {
  const _StatusCard({required this.state});

  final KycState state;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    final (background, foreground, title, body) = switch (state) {
      _ when state.isVerified => (
          scheme.primaryContainer,
          scheme.onPrimaryContainer,
          'Verified',
          'Reviewed ${state.latest?.reviewedAt == null ? '' : 'on ${formatDate(state.latest!.reviewedAt!)}'}.'
        ),
      _ when state.isPending => (
          scheme.secondaryContainer,
          scheme.onSecondaryContainer,
          'With a reviewer',
          'Sent ${state.latest == null ? 'recently' : formatAgo(state.latest!.submittedAt)}. '
              'Cash-out opens as soon as it clears.'
        ),
      _ when state.isRejected => (
          scheme.errorContainer,
          scheme.onErrorContainer,
          'Not accepted',
          // The reason is the entire value of a rejection. Without it the host resubmits exactly
          // the same thing and nothing improves.
          state.latest?.rejectionReason ?? 'Send your details again with a clearer photograph.'
        ),
      _ => (
          scheme.surfaceContainerHighest,
          scheme.onSurface,
          'Not started',
          'You can host and earn credits without this. Turning credits into money needs it.'
        ),
    };

    return Card(
      color: background,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              title,
              style: TextStyle(color: foreground, fontWeight: FontWeight.w700, fontSize: 16),
            ),
            const SizedBox(height: 6),
            Text(body, style: TextStyle(color: foreground)),
          ],
        ),
      ),
    );
  }
}

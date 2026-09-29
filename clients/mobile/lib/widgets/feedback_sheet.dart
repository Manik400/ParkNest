import 'dart:io' show Platform;

import 'package:flutter/material.dart';

import '../core/api.dart';
import '../core/api_client.dart';
import 'common.dart';

/// "Report a problem / Send feedback", mailed straight to the ParkNest team with the tester's
/// address as Reply-To. Works signed in or not: someone stuck at sign-in is who most needs it.
Future<void> showFeedbackSheet(
  BuildContext context, {
  String kind = 'Problem',
  required String page,
}) async {
  // Captured before the sheet: the context that opened it may be gone by the time it closes.
  final api = Services.of(context);
  final messenger = ScaffoldMessenger.of(context);

  final sent = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (_) => _FeedbackSheet(api: api, initialKind: kind, page: page),
  );

  if (sent == true) {
    messenger
      ..hideCurrentSnackBar()
      ..showSnackBar(const SnackBar(
        content: Text('Thank you — your message is on its way to the ParkNest team.'),
        behavior: SnackBarBehavior.floating,
      ));
  }
}

class _FeedbackSheet extends StatefulWidget {
  const _FeedbackSheet({required this.api, required this.initialKind, required this.page});

  final Api api;
  final String initialKind;
  final String page;

  @override
  State<_FeedbackSheet> createState() => _FeedbackSheetState();
}

class _FeedbackSheetState extends State<_FeedbackSheet> {
  static const _minLength = 10;
  static const _maxLength = 2000;

  static const _kinds = [
    (value: 'Problem', label: 'Report a problem'),
    (value: 'Idea', label: 'Suggest an idea'),
    (value: 'Other', label: 'Other feedback'),
  ];

  final _message = TextEditingController();
  final _email = TextEditingController();

  late String _kind = widget.initialKind;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _message.dispose();
    _email.dispose();
    super.dispose();
  }

  Future<void> _send() async {
    final message = _message.text.trim();
    if (message.length < _minLength) {
      setState(() => _error = 'Tell us a little more — a sentence or two is plenty.');
      return;
    }

    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      await widget.api.sendFeedback(
        kind: _kind,
        message: message,
        email: _email.text.trim().isEmpty ? null : _email.text.trim(),
        page: widget.page,
        client: '${Platform.operatingSystem} app',
      );
      if (mounted) Navigator.of(context).pop(true);
    } on ApiException catch (error) {
      if (mounted) {
        setState(() {
          _busy = false;
          _error = error.message;
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final signedIn = widget.api.session.isSignedIn;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 0, 20, 20 + MediaQuery.viewInsetsOf(context).bottom),
      child: SingleChildScrollView(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('Help us improve ParkNest', style: theme.textTheme.titleLarge),
            const SizedBox(height: 6),
            Text(
              'ParkNest is in public beta. Every problem report and idea is read, and shapes what gets fixed next.',
              style: theme.textTheme.bodySmall?.copyWith(color: theme.colorScheme.onSurfaceVariant),
            ),
            const SizedBox(height: 16),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final kind in _kinds)
                  ChoiceChip(
                    label: Text(kind.label),
                    selected: _kind == kind.value,
                    onSelected: _busy ? null : (_) => setState(() => _kind = kind.value),
                  ),
              ],
            ),
            const SizedBox(height: 16),
            TextField(
              controller: _message,
              enabled: !_busy,
              minLines: 4,
              maxLines: 8,
              maxLength: _maxLength,
              textCapitalization: TextCapitalization.sentences,
              decoration: InputDecoration(
                labelText: switch (_kind) {
                  'Problem' => 'What went wrong?',
                  'Idea' => 'What would make it better?',
                  _ => 'Your message',
                },
                hintText: _kind == 'Problem'
                    ? 'What you tried, what you expected, and what happened instead.'
                    : 'Tell us what you think.',
                alignLabelWithHint: true,
                border: const OutlineInputBorder(),
              ),
            ),
            const SizedBox(height: 8),
            TextField(
              controller: _email,
              enabled: !_busy,
              keyboardType: TextInputType.emailAddress,
              autofillHints: const [AutofillHints.email],
              decoration: InputDecoration(
                labelText: signedIn ? 'Your email (optional)' : 'Your email',
                hintText: signedIn ? 'Leave empty to use your account email' : 'So we can reply',
                border: const OutlineInputBorder(),
              ),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
            ],
            const SizedBox(height: 16),
            FilledButton(
              onPressed: _busy ? null : _send,
              child: _busy
                  ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Text('Send'),
            ),
          ],
        ),
      ),
    );
  }
}

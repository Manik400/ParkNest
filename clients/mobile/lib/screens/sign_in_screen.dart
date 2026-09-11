import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../core/api_client.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// An email address or phone number, then the code that arrives in the inbox or by SMS. No
/// password anywhere, which is the reason there is nothing to leak.
class SignInScreen extends StatefulWidget {
  const SignInScreen({super.key});

  @override
  State<SignInScreen> createState() => _SignInScreenState();
}

class _SignInScreenState extends State<SignInScreen> {
  final _destination = TextEditingController();
  final _code = TextEditingController();

  OtpChallenge? _challenge;
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    // The send button enables on a plausible entry, so it has to rebuild as the field changes.
    _destination.addListener(() => setState(() {}));
  }

  /// Loose on purpose: the API does the real validation and says what is wrong.
  bool get _plausible {
    final value = _destination.text.trim();
    return value.contains('@')
        ? value.length >= 5
        : value.replaceAll(RegExp(r'\D'), '').length >= 10;
  }

  @override
  void dispose() {
    _destination.dispose();
    _code.dispose();
    super.dispose();
  }

  Future<void> _requestCode() async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final challenge = await Services.of(context).requestOtp(_destination.text.trim());

      if (!mounted) return;

      setState(() {
        _challenge = challenge;
        // With no real email or SMS sender configured the API hands back the code. Filling it in
        // beats making someone read it off a screen and retype it.
        _code.text = challenge.devCode ?? '';
      });
    } on ApiException catch (error) {
      if (!mounted) return;

      // A 429 here is the resend cooldown or the hourly cap. Both are expected, and the API says
      // when to come back, so pass that on rather than a bare "too many requests".
      setState(() => _error = error.retryAfter == null
          ? error.message
          : '${error.message} (about ${error.retryAfter!.inSeconds}s)');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _verify() async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      // The router is watching the session and moves off this screen by itself once it changes.
      await Services.of(context).verifyOtp(_destination.text.trim(), _code.text.trim());
    } on ApiException catch (error) {
      if (!mounted) return;
      setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final awaitingCode = _challenge != null;

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Icon(Icons.local_parking_rounded, size: 56, color: theme.colorScheme.primary),
                  const SizedBox(height: 16),
                  Text('ParkNest', style: theme.textTheme.headlineMedium, textAlign: TextAlign.center),
                  const SizedBox(height: 8),
                  Text(
                    'Park in someone’s driveway. Pay by the slot.',
                    textAlign: TextAlign.center,
                    style: TextStyle(color: theme.colorScheme.onSurfaceVariant),
                  ),
                  const SizedBox(height: 32),

                  TextField(
                    controller: _destination,
                    enabled: !awaitingCode && !_busy,
                    keyboardType: TextInputType.emailAddress,
                    autofillHints: const [AutofillHints.email],
                    autocorrect: false,
                    decoration: const InputDecoration(
                      labelText: 'Email or phone number',
                      prefixIcon: Icon(Icons.alternate_email),
                    ),
                  ),

                  if (awaitingCode) ...[
                    const SizedBox(height: 12),
                    TextField(
                      controller: _code,
                      enabled: !_busy,
                      keyboardType: TextInputType.number,
                      maxLength: 6,
                      inputFormatters: [FilteringTextInputFormatter.digitsOnly],
                      decoration: const InputDecoration(
                        labelText: 'Six-digit code',
                        prefixIcon: Icon(Icons.password_outlined),
                        counterText: '',
                      ),
                    ),
                    if (_challenge?.devCode != null)
                      Padding(
                        padding: const EdgeInsets.only(top: 4),
                        child: Text(
                          'Development build: the API returned this code because no email or SMS '
                          'sender is configured.',
                          style: theme.textTheme.bodySmall
                              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                        ),
                      ),
                  ],

                  if (_error != null) ...[
                    const SizedBox(height: 16),
                    Text(_error!, style: TextStyle(color: theme.colorScheme.error)),
                  ],

                  const SizedBox(height: 24),

                  FilledButton(
                    onPressed: _busy || !_plausible
                        ? null
                        : (awaitingCode ? _verify : _requestCode),
                    child: _busy
                        ? const SizedBox(
                            height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2))
                        : Text(awaitingCode ? 'Sign in' : 'Send code'),
                  ),

                  if (awaitingCode)
                    TextButton(
                      onPressed: _busy
                          ? null
                          : () => setState(() {
                                _challenge = null;
                                _code.clear();
                                _error = null;
                              }),
                      child: const Text('Use a different email or number'),
                    ),

                  const SizedBox(height: 8),
                  Text(
                    'First sign-in creates your account.',
                    textAlign: TextAlign.center,
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

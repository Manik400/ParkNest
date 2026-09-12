import 'package:flutter/material.dart';

import '../core/api_client.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// The account as its owner sees it.
///
/// Sign-in contacts are shown, not edited: an unverified number attached here could sign in as
/// this account, so a contact changes only by signing in with it. The payment number is the
/// exception — it goes only to the payment gateway, which insists on one, and needs no code.
class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key});

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  late Future<Profile> _profile;
  final _name = TextEditingController();
  final _paymentPhone = TextEditingController();
  bool _busy = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _profile = Services.of(context).myProfile().then((me) {
      _name.text = me.fullName;
      _paymentPhone.text = me.paymentPhone ?? '';
      return me;
    });
  }

  @override
  void dispose() {
    _name.dispose();
    _paymentPhone.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    final api = Services.of(context);
    setState(() => _busy = true);

    try {
      final me = await api.updateProfile(
        fullName: _name.text,
        // Empty clears the saved number; the API treats null as "leave alone".
        paymentPhone: _paymentPhone.text,
      );
      if (!mounted) return;
      setState(() => _profile = Future.value(me));
      showMessage(context, 'Saved.');
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Profile')),
      body: FutureBuilder<Profile>(
        future: _profile,
        builder: (context, snapshot) => AsyncView<Profile>(
          snapshot: snapshot,
          builder: (me) => ListView(
            padding: const EdgeInsets.all(16),
            children: [
              SectionCard(
                title: 'Sign-in',
                child: Column(
                  children: [
                    FactRow('Email', me.email ?? '—'),
                    FactRow('Phone', me.phone ?? '—'),
                    FactRow('Role', me.role),
                  ],
                ),
              ),
              const SizedBox(height: 16),
              TextField(
                controller: _name,
                textCapitalization: TextCapitalization.words,
                decoration: const InputDecoration(labelText: 'Name'),
              ),
              if (me.phone == null) ...[
                const SizedBox(height: 16),
                TextField(
                  controller: _paymentPhone,
                  keyboardType: TextInputType.phone,
                  autofillHints: const [AutofillHints.telephoneNumber],
                  decoration: const InputDecoration(
                    labelText: 'Mobile number for payments',
                    helperText:
                        'The payment provider needs one on every order. Used only for that; '
                        'not a sign-in number. Leave empty to be asked at checkout.',
                    helperMaxLines: 3,
                  ),
                ),
              ],
              const SizedBox(height: 24),
              FilledButton(
                onPressed: _busy ? null : _save,
                child: Text(_busy ? 'Saving…' : 'Save'),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

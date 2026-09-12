import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/api.dart';
import '../core/api_client.dart';
import '../core/config.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import 'home_shell.dart';

/// Balance, history, and buying credits.
///
/// The three buckets are shown separately rather than as one number because they are not
/// interchangeable: held credits are already committed to a booking, and earnings can only leave
/// by cash-out. Collapsing them into a "balance" would let someone plan a booking against money
/// that is not theirs to spend.
class WalletScreen extends StatefulWidget {
  const WalletScreen({super.key});

  @override
  State<WalletScreen> createState() => _WalletScreenState();
}

class _WalletScreenState extends State<WalletScreen> {
  late Future<({Wallet wallet, List<LedgerEntrySummary> entries, KycState kyc})> _data;

  bool _awaitingPayment = false;

  /// The gateway refused to open a checkout without a mobile number, and the account has none.
  /// Once set, the amount sheet asks for one alongside the amount.
  bool _askPhone = false;
  bool _cashingOut = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    final api = Services.of(context);

    setState(() {
      // The verification state is fetched with the balance because cash-out is the only thing it
      // affects, and a screen that offers a button the server will refuse is worse than one that
      // explains why the button is not there yet.
      _data = Future.wait([api.myWallet(), api.myTransactions(), api.kycState()])
          .then((results) => (
                wallet: results[0] as Wallet,
                entries: results[1] as List<LedgerEntrySummary>,
                kyc: results[2] as KycState,
              ));
    });
  }

  Future<void> _reload() async {
    _load();
    await _data;
  }

  /// Asks for the money to be sent to the host's bank.
  ///
  /// The credits leave the earning balance the moment this is accepted, before any transfer has
  /// been made — so they cannot be spent twice while one is in flight — and come back through a
  /// compensating refund if the transfer is later rejected.
  Future<void> _cashOut(double earning) async {
    final api = Services.of(context);
    final controller = TextEditingController(text: earning.toStringAsFixed(0));

    final amount = await showDialog<double>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cash out earnings'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            TextField(
              controller: controller,
              autofocus: true,
              keyboardType: const TextInputType.numberWithOptions(decimal: true),
              decoration: const InputDecoration(labelText: 'Credits', prefixText: '\u20b9 '),
            ),
            const SizedBox(height: 12),
            const Text(
              'The credits leave your earnings straight away. An operator makes the transfer, '
              'and if their bank rejects it the credits come back.',
              style: TextStyle(fontSize: 13),
            ),
          ],
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Not now')),
          FilledButton(
            onPressed: () => Navigator.pop(context, double.tryParse(controller.text.trim())),
            child: const Text('Request'),
          ),
        ],
      ),
    );

    if (amount == null || amount <= 0 || !mounted) return;

    setState(() => _cashingOut = true);

    try {
      // A fresh key per request: this is the client's promise that a retried tap is the same
      // withdrawal, not a second one.
      await api.cashOut(amount, 'cashout-${DateTime.now().microsecondsSinceEpoch}');
      if (mounted) showMessage(context, 'Requested. You will be told when it is paid.');
      _load();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _cashingOut = false);
    }
  }

  Future<void> _addCredits() async {
    // Captured before the sheet, not after: past the await this context may be gone, and the
    // payment poll below outlives the sheet by minutes.
    final api = Services.of(context);

    final topUp = await showModalBottomSheet<_TopUp>(
      context: context,
      isScrollControlled: true,
      builder: (_) => _AmountSheet(askPhone: _askPhone),
    );

    if (topUp == null) return;

    try {
      final order = await api.startPayment(topUp.amount, phone: topUp.phone);

      final checkoutUrl = order.checkoutUrl(AppConfig.apiBaseUrl);

      if (checkoutUrl == null) {
        if (mounted) {
          showError(context, 'This gateway has no hosted checkout page to open.');
        }
        return;
      }

      // The checkout goes to the system browser rather than an in-app webview, so the user can see
      // the address they are handing card details to. That is worth more than a seamless frame.
      final launched = await launchUrl(
        Uri.parse(checkoutUrl),
        mode: LaunchMode.externalApplication,
      );

      if (!launched) {
        if (mounted) showError(context, 'Could not open the checkout page.');
        return;
      }

      if (mounted) setState(() => _awaitingPayment = true);

      // The browser coming back proves the user pressed a button, not that the money arrived.
      // Only the order status does, because only a signed webhook sets it.
      final settled = await _pollOrder(api, order.orderId);

      if (!mounted) return;

      setState(() => _awaitingPayment = false);

      if (settled == null) {
        showMessage(context, 'Still waiting on the payment. Pull to refresh in a moment.');
      } else if (settled.status == 'Paid') {
        showMessage(context, '${formatCredits(settled.amount)} added.');
      } else {
        showError(context, settled.failureReason ?? 'The payment did not go through.');
      }

      _load();
    } on ApiException catch (error) {
      if (!mounted) return;

      setState(() => _awaitingPayment = false);

      // The interceptor carries the API's message, not its problem type, so the phrase the
      // PaymentPhoneRequiredException always uses is what identifies it. Ask once, then retry.
      if (!_askPhone && error.message.toLowerCase().contains('mobile number')) {
        setState(() => _askPhone = true);
        showError(context, error);
        return _addCredits();
      }

      showError(context, error);
    }
  }

  /// Polls until the order settles or we give up. Gives up rather than spinning forever: a webhook
  /// that never lands is a real outcome, and a permanent spinner tells the user nothing.
  Future<PaymentOrderView?> _pollOrder(Api api, String orderId) async {
    final deadline = DateTime.now().add(AppConfig.paymentPollTimeout);

    while (DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(AppConfig.paymentPollInterval);

      if (!mounted) return null;

      try {
        final order = await api.paymentOrder(orderId);
        if (order.isSettled) return order;
      } on ApiException {
        // One failed poll costs one attempt, not the whole wait. The webhook may simply not have
        // landed yet, and giving up on a transient error would report a successful payment as lost.
        continue;
      }
    }

    return null;
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Wallet'),
        actions: const [NotificationsBell(), HomeMenuButton()],
      ),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<({Wallet wallet, List<LedgerEntrySummary> entries, KycState kyc})>(
          future: _data,
          builder: (context, snapshot) =>
              AsyncView<({Wallet wallet, List<LedgerEntrySummary> entries, KycState kyc})>(
            snapshot: snapshot,
            onRetry: _reload,
            builder: (data) => _build(data.wallet, data.entries, data.kyc),
          ),
        ),
      ),
    );
  }

  Widget _build(Wallet wallet, List<LedgerEntrySummary> entries, KycState kyc) {
    final scheme = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 32),
      children: [
        Card(
          color: scheme.primaryContainer,
          child: Padding(
            padding: const EdgeInsets.all(20),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('Spendable', style: TextStyle(color: scheme.onPrimaryContainer)),
                const SizedBox(height: 4),
                Text(
                  formatCredits(wallet.spendable),
                  style: TextStyle(
                    color: scheme.onPrimaryContainer,
                    fontSize: 34,
                    fontWeight: FontWeight.w700,
                  ),
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Expanded(
                      child: _Bucket(
                        label: 'Held for bookings',
                        value: wallet.held,
                        color: scheme.onPrimaryContainer,
                      ),
                    ),
                    Expanded(
                      child: _Bucket(
                        label: 'Earned as host',
                        value: wallet.earning,
                        color: scheme.onPrimaryContainer,
                      ),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
        const SizedBox(height: 16),

        FilledButton.icon(
          onPressed: _awaitingPayment ? null : _addCredits,
          icon: _awaitingPayment
              ? const SizedBox(height: 18, width: 18, child: CircularProgressIndicator(strokeWidth: 2))
              : const Icon(Icons.add),
          label: Text(_awaitingPayment ? 'Waiting for the payment…' : 'Add credits'),
        ),

        // Only for someone who has actually earned. A renter has no use for a cash-out button,
        // and an empty one invites the question of what it would pay out.
        if (wallet.earning > 0) ...[
          const SizedBox(height: 16),
          _CashOutCard(
            earning: wallet.earning,
            kyc: kyc,
            busy: _cashingOut,
            onCashOut: _cashOut,
          ),
        ],

        const SizedBox(height: 24),
        Text('History', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),

        if (entries.isEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 24),
            child: Text(
              'Nothing yet. Add credits to book your first space.',
              style: TextStyle(color: scheme.onSurfaceVariant),
            ),
          )
        else
          for (final entry in entries) _HistoryRow(entry: entry),
      ],
    );
  }
}

class _Bucket extends StatelessWidget {
  const _Bucket({required this.label, required this.value, required this.color});

  final String label;
  final double value;
  final Color color;

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(label, style: TextStyle(color: color.withOpacity(0.75), fontSize: 12)),
        Text(
          formatCredits(value),
          style: TextStyle(color: color, fontWeight: FontWeight.w600),
        ),
      ],
    );
  }
}

class _HistoryRow extends StatelessWidget {
  const _HistoryRow({required this.entry});

  final LedgerEntrySummary entry;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return ListTile(
      contentPadding: EdgeInsets.zero,
      title: Text(entry.description ?? entry.transactionType),
      subtitle: Text(formatDateTime(entry.createdAt)),
      trailing: Text(
        '${entry.isCredit ? '+' : '−'}${formatCredits(entry.amount)}',
        style: TextStyle(
          fontWeight: FontWeight.w600,
          color: entry.isCredit ? scheme.primary : scheme.onSurface,
        ),
      ),
    );
  }
}

/// What the amount sheet hands back: how much, and a mobile number when the gateway asked for one.
class _TopUp {
  const _TopUp(this.amount, this.phone);

  final double amount;
  final String? phone;
}

/// Amount picker. Presets rather than a bare field, because the common case is topping up enough
/// for a few sessions and nobody wants to think in exact figures for that.
class _AmountSheet extends StatefulWidget {
  const _AmountSheet({required this.askPhone});

  /// Also collect a mobile number: the gateway will not open a checkout without one and the
  /// account has none on file. Off by default, because most accounts do.
  final bool askPhone;

  @override
  State<_AmountSheet> createState() => _AmountSheetState();
}

class _AmountSheetState extends State<_AmountSheet> {
  final _controller = TextEditingController(text: '500');
  final _phone = TextEditingController();

  static const _presets = [250.0, 500.0, 1000.0, 2500.0];

  @override
  void dispose() {
    _controller.dispose();
    _phone.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final amount = double.tryParse(_controller.text) ?? 0;
    final phone = _phone.text.trim();
    // The API validates the number properly; this only stops an obviously empty one round-tripping.
    final ready = amount > 0 && (!widget.askPhone || phone.length >= 10);

    return Padding(
      padding: EdgeInsets.only(
        left: 20,
        right: 20,
        top: 20,
        bottom: MediaQuery.of(context).viewInsets.bottom + 20,
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('Add credits', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 16),
          Wrap(
            spacing: 8,
            children: [
              for (final preset in _presets)
                ChoiceChip(
                  label: Text(formatCredits(preset)),
                  selected: amount == preset,
                  onSelected: (_) => setState(() => _controller.text = preset.toStringAsFixed(0)),
                ),
            ],
          ),
          const SizedBox(height: 16),
          TextField(
            controller: _controller,
            keyboardType: TextInputType.number,
            onChanged: (_) => setState(() {}),
            decoration: const InputDecoration(labelText: 'Amount', prefixText: '₹ '),
          ),
          if (widget.askPhone) ...[
            const SizedBox(height: 12),
            TextField(
              controller: _phone,
              keyboardType: TextInputType.phone,
              autofillHints: const [AutofillHints.telephoneNumber],
              onChanged: (_) => setState(() {}),
              decoration: const InputDecoration(
                labelText: 'Mobile number',
                helperText: 'The payment provider needs one; it is not saved to your account.',
              ),
            ),
          ],
          const SizedBox(height: 16),
          FilledButton(
            // The API enforces the real minimum and maximum; this only stops an obviously empty
            // submission from becoming a round trip.
            onPressed: !ready
                ? null
                : () => Navigator.pop(context, _TopUp(amount, widget.askPhone ? phone : null)),
            child: const Text('Continue to payment'),
          ),
        ],
      ),
    );
  }
}

/// Earnings, and the one thing standing between them and a bank account.
class _CashOutCard extends StatelessWidget {
  const _CashOutCard({
    required this.earning,
    required this.kyc,
    required this.busy,
    required this.onCashOut,
  });

  final double earning;
  final KycState kyc;
  final bool busy;
  final Future<void> Function(double earning) onCashOut;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return SectionCard(
      title: 'Your earnings',
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(
            '${formatCredits(earning)} earned from hosting.',
            style: TextStyle(color: scheme.onSurfaceVariant),
          ),
          const SizedBox(height: 12),
          if (kyc.canCashOut)
            FilledButton.icon(
              onPressed: busy ? null : () => onCashOut(earning),
              icon: const Icon(Icons.account_balance_outlined),
              label: Text(busy ? 'Requesting\u2026' : 'Cash out'),
            )
          else ...[
            Text(
              kyc.isPending
                  ? 'Your identity check is with a reviewer. Cash-out opens as soon as it clears.'
                  : 'Money leaving the platform needs an identity check first — the credits sit in '
                      'escrow, not in a wallet we issue.',
              style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
            ),
            const SizedBox(height: 10),
            OutlinedButton(
              onPressed: () => context.push('/kyc'),
              child: Text(kyc.isPending ? 'See your submission' : 'Verify your identity'),
            ),
          ],
        ],
      ),
    );
  }
}

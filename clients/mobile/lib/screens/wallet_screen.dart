import 'dart:async';

import 'package:flutter/material.dart';
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
  late Future<({Wallet wallet, List<LedgerEntrySummary> entries})> _data;

  bool _awaitingPayment = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    final api = Services.of(context);

    setState(() {
      _data = Future.wait([api.myWallet(), api.myTransactions()])
          .then((results) => (
                wallet: results[0] as Wallet,
                entries: results[1] as List<LedgerEntrySummary>,
              ));
    });
  }

  Future<void> _reload() async {
    _load();
    await _data;
  }

  Future<void> _addCredits() async {
    // Captured before the sheet, not after: past the await this context may be gone, and the
    // payment poll below outlives the sheet by minutes.
    final api = Services.of(context);

    final amount = await showModalBottomSheet<double>(
      context: context,
      isScrollControlled: true,
      builder: (_) => const _AmountSheet(),
    );

    if (amount == null) return;

    try {
      final order = await api.startPayment(amount);

      // The checkout goes to the system browser rather than an in-app webview, so the user can see
      // the address they are handing card details to. That is worth more than a seamless frame.
      final uri = Uri.parse(order.checkoutPayload);
      final launched = await launchUrl(uri, mode: LaunchMode.externalApplication);

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
      if (mounted) {
        setState(() => _awaitingPayment = false);
        showError(context, error);
      }
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
      appBar: AppBar(title: const Text('Wallet'), actions: const [HomeMenuButton()]),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<({Wallet wallet, List<LedgerEntrySummary> entries})>(
          future: _data,
          builder: (context, snapshot) => AsyncView<({Wallet wallet, List<LedgerEntrySummary> entries})>(
            snapshot: snapshot,
            onRetry: _reload,
            builder: (data) => _build(data.wallet, data.entries),
          ),
        ),
      ),
    );
  }

  Widget _build(Wallet wallet, List<LedgerEntrySummary> entries) {
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

/// Amount picker. Presets rather than a bare field, because the common case is topping up enough
/// for a few sessions and nobody wants to think in exact figures for that.
class _AmountSheet extends StatefulWidget {
  const _AmountSheet();

  @override
  State<_AmountSheet> createState() => _AmountSheetState();
}

class _AmountSheetState extends State<_AmountSheet> {
  final _controller = TextEditingController(text: '500');

  static const _presets = [250.0, 500.0, 1000.0, 2500.0];

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final amount = double.tryParse(_controller.text) ?? 0;

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
          const SizedBox(height: 16),
          FilledButton(
            // The API enforces the real minimum and maximum; this only stops an obviously empty
            // submission from becoming a round trip.
            onPressed: amount <= 0 ? null : () => Navigator.pop(context, amount),
            child: const Text('Continue to payment'),
          ),
        ],
      ),
    );
  }
}

import 'package:flutter/material.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// One booking: the session controls, the money, and the ledger trail behind it.
///
/// The ledger is shown to the renter deliberately. The whole settlement model rests on credits
/// having been reserved before the car arrived, and the only way that reads as fair rather than
/// opaque is if the person being charged can see every movement that produced the figure.
class BookingDetailScreen extends StatefulWidget {
  const BookingDetailScreen({required this.bookingId, super.key});

  final String bookingId;

  @override
  State<BookingDetailScreen> createState() => _BookingDetailScreenState();
}

class _BookingDetailScreenState extends State<BookingDetailScreen> {
  late Future<BookingDetail> _booking;
  SessionOutcome? _outcome;
  bool _busy = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _booking = Services.of(context).booking(widget.bookingId);
  }

  void _reload() {
    setState(() => _booking = Services.of(context).booking(widget.bookingId));
  }

  Future<void> _run(Future<void> Function() action) async {
    setState(() => _busy = true);

    try {
      await action();
      if (mounted) _reload();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _checkIn() => _run(() => Services.of(context).startSession(widget.bookingId));

  Future<void> _checkOut() => _run(() async {
        final outcome = await Services.of(context).endSession(widget.bookingId);
        if (mounted) setState(() => _outcome = outcome);
      });

  Future<void> _cancel() async {
    final api = Services.of(context);

    // Asked rather than assumed: cancelling late costs a fee, and the size of it is a server-side
    // policy. Telling the user "you get everything back" and then taking half would be the worst
    // possible way for them to learn the rule.
    final CancellationTerms terms;

    try {
      terms = await api.cancellationTerms(widget.bookingId);
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
      return;
    }

    if (!mounted) return;

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Cancel this booking?'),
        content: Text(
          terms.isFree
              ? 'The whole hold, ${formatCredits(terms.refund)}, goes back to your spendable '
                  'balance.'
              : 'Cancelling now costs ${formatCredits(terms.fee)} — the host loses a slot they '
                  'cannot re-let this close to the start. '
                  '${formatCredits(terms.refund)} comes back to you.',
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Keep it')),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Cancel booking'),
          ),
        ],
      ),
    );

    if (confirmed ?? false) {
      await _run(() => api.cancelBooking(widget.bookingId));
    }
  }

  Future<void> _raiseDispute() async {
    // Resolved before the dialog: after an await this State may be looking at a context that is no
    // longer mounted, and reading an InheritedWidget off it is the classic use-after-dispose.
    final api = Services.of(context);
    final controller = TextEditingController();

    final reason = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('What went wrong?'),
        content: TextField(
          controller: controller,
          autofocus: true,
          maxLines: 4,
          decoration: const InputDecoration(hintText: 'The space was blocked when I arrived…'),
        ),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context), child: const Text('Never mind')),
          FilledButton(
            onPressed: () => Navigator.pop(context, controller.text.trim()),
            child: const Text('Raise dispute'),
          ),
        ],
      ),
    );

    if (reason == null || reason.isEmpty) {
      return;
    }

    try {
      await api.raiseDispute(widget.bookingId, reason);
      if (mounted) showMessage(context, 'Dispute raised. An operator will review it.');
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Booking')),
      body: FutureBuilder<BookingDetail>(
        future: _booking,
        builder: (context, snapshot) => AsyncView<BookingDetail>(
          snapshot: snapshot,
          onRetry: _reload,
          builder: _build,
        ),
      ),
    );
  }

  Widget _build(BookingDetail booking) {
    final scheme = Theme.of(context).colorScheme;
    final summary = booking.summary;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 32),
      children: [
        Row(
          children: [
            Expanded(
              child: Text(summary.spaceTitle, style: Theme.of(context).textTheme.titleLarge),
            ),
            StatusChip(summary.status),
          ],
        ),
        const SizedBox(height: 4),
        Text(summary.spaceAddress, style: TextStyle(color: scheme.onSurfaceVariant)),
        const SizedBox(height: 20),

        // A shortfall means the renter left owing credits. It is the most consequential thing that
        // can appear on this screen, so it is stated plainly rather than left to be inferred from
        // the figures below.
        if (booking.shortfallAmount > 0) ...[
          Card(
            color: scheme.errorContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    '${formatCredits(booking.shortfallAmount)} uncovered',
                    style: TextStyle(
                      color: scheme.onErrorContainer,
                      fontWeight: FontWeight.w700,
                      fontSize: 16,
                    ),
                  ),
                  const SizedBox(height: 6),
                  Text(
                    'The overstay could not be covered by your balance. Your access is '
                    'restricted until it is settled.',
                    style: TextStyle(color: scheme.onErrorContainer),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 16),
        ],

        if (_outcome case final outcome?) ...[
          Card(
            color: scheme.primaryContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                'Settled ${formatDuration(outcome.billedMinutes)} for '
                '${formatCredits(outcome.totalCharged)}. '
                '${formatCredits(outcome.releasedToRenter)} came back to you.',
                style: TextStyle(color: scheme.onPrimaryContainer),
              ),
            ),
          ),
          const SizedBox(height: 16),
        ],

        // Tier 1 detection: the renter tapping these. Tier 2 and 3 replace the tap with a QR scan
        // or a plate read without changing anything about what happens to the money.
        if (summary.status == 'Held') ...[
          FilledButton.icon(
            onPressed: _busy ? null : _checkIn,
            icon: const Icon(Icons.local_parking),
            label: const Text('I have parked'),
          ),
          const SizedBox(height: 8),
          OutlinedButton(
            onPressed: _busy ? null : _cancel,
            child: const Text('Cancel booking'),
          ),
          const SizedBox(height: 20),
        ] else if (summary.status == 'Active') ...[
          FilledButton.icon(
            onPressed: _busy ? null : _checkOut,
            icon: const Icon(Icons.exit_to_app),
            label: const Text('I am leaving'),
          ),
          const SizedBox(height: 8),
          Text(
            'Checking out measures the real duration and settles it. Staying past '
            '${formatTime(summary.expectedEndTime)} bills the overstay automatically.',
            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
          ),
          const SizedBox(height: 20),
        ],

        SectionCard(
          title: 'Session',
          child: Column(
            children: [
              FactRow('Vehicle', booking.vehiclePlate),
              FactRow('Booked start', formatDateTime(summary.startTime)),
              FactRow('Expected end', formatDateTime(summary.expectedEndTime)),
              FactRow(
                'Checked in',
                booking.actualStartTime == null ? '—' : formatDateTime(booking.actualStartTime!),
              ),
              FactRow(
                'Checked out',
                summary.actualEndTime == null ? '—' : formatDateTime(summary.actualEndTime!),
              ),
              FactRow('Booked', formatDuration(booking.bookedMinutes)),
              FactRow(
                'Billed',
                booking.billedMinutes == null ? '—' : formatDuration(booking.billedMinutes!),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),

        SectionCard(
          title: 'Money',
          child: Column(
            children: [
              FactRow('Rate', '${formatCredits(summary.ratePerHour)} / hr'),
              FactRow('Held up front', formatCredits(summary.holdAmount)),
              if (booking.overstayAmount > 0)
                FactRow('Overstay', formatCredits(booking.overstayAmount)),
              FactRow('Settled', formatCredits(summary.settledAmount), emphasised: true),
            ],
          ),
        ),
        const SizedBox(height: 16),

        if (summary.status != 'Held' && summary.status != 'Cancelled') ...[
          OutlinedButton.icon(
            onPressed: _raiseDispute,
            icon: const Icon(Icons.flag_outlined),
            label: const Text('Something was wrong'),
          ),
          const SizedBox(height: 16),
        ],

        SectionCard(
          title: 'Credit movements',
          child: booking.ledgerEntries.isEmpty
              ? Text(
                  'Nothing has settled yet — credits are reserved but no money has moved.',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                )
              : Column(
                  children: [
                    for (final entry in booking.ledgerEntries) _LedgerRow(entry: entry),
                  ],
                ),
        ),
      ],
    );
  }
}

class _LedgerRow extends StatelessWidget {
  const _LedgerRow({required this.entry});

  final LedgerEntrySummary entry;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  humanise(entry.transactionType),
                  style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 14),
                ),
                Text(
                  '${humanise(entry.account)} · ${formatDateTime(entry.createdAt)}',
                  style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                ),
              ],
            ),
          ),
          Text(
            // The sign is the point: a renter reading this needs to see at a glance which way each
            // movement went, not work it out from the account name.
            '${entry.isCredit ? '+' : '−'}${formatCredits(entry.amount)}',
            style: TextStyle(
              fontWeight: FontWeight.w600,
              color: entry.isCredit ? scheme.primary : scheme.onSurface,
            ),
          ),
        ],
      ),
    );
  }

  static String humanise(String value) =>
      value.replaceAllMapped(RegExp(r'([a-z])([A-Z])'), (m) => '${m[1]} ${m[2]}');
}

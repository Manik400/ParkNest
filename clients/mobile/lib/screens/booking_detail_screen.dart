import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/api.dart';
import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/location.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import 'scan_check_in_screen.dart';

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

  Future<void> _checkIn({CheckInProof? proof}) =>
      _run(() => Services.of(context).startSession(widget.bookingId, proof: proof));

  Future<void> _checkOut({CheckInProof? proof}) => _run(() async {
        final outcome = await Services.of(context).endSession(widget.bookingId, proof: proof);
        if (mounted) setState(() => _outcome = outcome);
      });

  /// Tier 2: the sticker's code plus where the phone was when it read it.
  ///
  /// Both halves or nothing. A scan with no fix is not a weaker proof the server could accept
  /// with a caveat — it is a photographed sticker, which is exactly what the geofence exists to
  /// catch — so a failed fix falls back to the honest Tier 1 tap rather than half a Tier 2 claim.
  Future<void> _scanThen(Future<void> Function({CheckInProof? proof}) action) async {
    final token = await Navigator.of(context).push<String>(
      MaterialPageRoute(builder: (_) => const ScanCheckInScreen()),
    );

    if (token == null || !mounted) return;

    setState(() => _busy = true);
    final fix = await const LocationService().current(precise: true);
    if (!mounted) return;
    setState(() => _busy = false);

    if (!fix.isSuccess) {
      showError(context, '${fix.message} Confirming in the app works without it.');
      return;
    }

    await action(
      proof: CheckInProof(
        token: token,
        latitude: fix.latitude!,
        longitude: fix.longitude!,
      ),
    );
  }

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
          // Three sentences, not two. "Free" and "free because we could not give you the space"
          // are different facts, and a renter told only the first will read a failed booking as
          // their own change of mind.
          terms.slotBlocked
              ? 'The previous car had not left when your slot came due, so this costs you '
                  'nothing however late it is. The whole hold, '
                  '${formatCredits(terms.refund)}, goes back to your spendable balance.'
              : terms.isFree
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

        // The space was still occupied when this slot came due. Placed above everything else on
        // the screen because it is the only thing here that changes what the renter should do
        // next, and the status chip cannot say it — the booking is still, correctly, Held.
        if (booking.blockedByBookingId != null) ...[
          Card(
            color: scheme.errorContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'The space may still be occupied',
                    style: TextStyle(
                      color: scheme.onErrorContainer,
                      fontWeight: FontWeight.w700,
                      fontSize: 16,
                    ),
                  ),
                  const SizedBox(height: 6),
                  Text(
                    'The previous car had not left when your slot was due to start. '
                    'Cancelling this booking costs you nothing.',
                    style: TextStyle(color: scheme.onErrorContainer),
                  ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 16),
        ],

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
          OutlinedButton.icon(
            onPressed: _busy ? null : () => _scanThen(_checkIn),
            icon: const Icon(Icons.qr_code_scanner),
            label: const Text('Scan the code at the space'),
          ),
          const SizedBox(height: 8),
          Text(
            'Scanning records that you were there, which is worth more than your word if the '
            'session is ever disputed. Not every space has a sticker.',
            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
          ),
          const SizedBox(height: 12),
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
          OutlinedButton.icon(
            onPressed: _busy ? null : () => _scanThen(_checkOut),
            icon: const Icon(Icons.qr_code_scanner),
            label: const Text('Scan on the way out'),
          ),
          const SizedBox(height: 8),
          Text(
            'Checking out measures the real duration and settles it. Staying past '
            '${formatTime(summary.expectedEndTime)} bills the overstay automatically.',
            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
          ),
          const SizedBox(height: 20),
        ],

        // Only for a session that happened. The server decides whether a rating is still owed —
        // "finished, and you have not already had your say" is its rule, and a copy of it here
        // would eventually offer a form the API refuses.
        if (summary.status != 'Held' && summary.status != 'Cancelled') ...[
          _RatingSection(bookingId: widget.bookingId),
          const SizedBox(height: 16),
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

/// The rating a finished session is still waiting on, and the way to give it.
///
/// Its own widget with its own request because it must not delay the booking: the ledger and the
/// settlement are what the renter opened this screen for, and a prompt that has not loaded yet is
/// no reason to hold them back.
class _RatingSection extends StatefulWidget {
  const _RatingSection({required this.bookingId});

  final String bookingId;

  @override
  State<_RatingSection> createState() => _RatingSectionState();
}

class _RatingSectionState extends State<_RatingSection> {
  Future<RatingPrompt>? _prompt;
  int _score = 0;
  bool _submitting = false;
  bool _submitted = false;

  final TextEditingController _comment = TextEditingController();

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _prompt ??= Services.of(context).ratingPrompt(widget.bookingId);
  }

  @override
  void dispose() {
    _comment.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    final api = Services.of(context);
    setState(() => _submitting = true);

    try {
      await api.rate(widget.bookingId, _score, comment: _comment.text.trim());
      if (mounted) setState(() => _submitted = true);
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    if (_submitted) {
      return SectionCard(
        title: 'Your rating',
        child: Text(
          'Thank you — it counts towards their trust score.',
          style: TextStyle(color: scheme.onSurfaceVariant),
        ),
      );
    }

    return FutureBuilder<RatingPrompt>(
      future: _prompt,
      builder: (context, snapshot) {
        final prompt = snapshot.data;

        // A prompt that failed to load, or has not arrived, shows nothing at all. There is no
        // useful action for the user in "could not check whether you owe a rating".
        if (prompt == null) {
          return const SizedBox.shrink();
        }

        if (!prompt.canRate) {
          return prompt.aboutUserId == null
              ? const SizedBox.shrink()
              : SectionCard(
                  title: 'Rating',
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      Text(
                        prompt.reason ?? 'Nothing to rate here.',
                        style: TextStyle(color: scheme.onSurfaceVariant),
                      ),
                      const SizedBox(height: 12),
                      OutlinedButton(
                        onPressed: () => context.push('/users/${prompt.aboutUserId}'),
                        child: const Text('See their reputation'),
                      ),
                    ],
                  ),
                );
        }

        return SectionCard(
          title: 'How did it go?',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'Rating the other side is what makes a good host or a careful renter visible to '
                'the next person.',
                style: TextStyle(color: scheme.onSurfaceVariant),
              ),
              const SizedBox(height: 12),
              StarPicker(
                score: _score,
                onChanged: _submitting ? null : (value) => setState(() => _score = value),
              ),
              const SizedBox(height: 12),
              TextField(
                controller: _comment,
                enabled: !_submitting,
                maxLines: 3,
                maxLength: 500,
                decoration: const InputDecoration(
                  hintText: 'Anything worth saying? (optional)',
                ),
              ),
              const SizedBox(height: 4),
              FilledButton(
                // No score, no submission: a rating is the score, and the comment alone has
                // nowhere to go.
                onPressed: _score == 0 || _submitting ? null : _submit,
                child: const Text('Submit rating'),
              ),
            ],
          ),
        );
      },
    );
  }
}

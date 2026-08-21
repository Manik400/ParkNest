import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import 'home_shell.dart';

/// The renter's own bookings, open ones first.
///
/// Ordering matters more here than it looks: a session that is running, or one that is overdue and
/// quietly billing an overstay, is the only thing on this screen the user might need to act on in
/// the next minute. Burying it under last month's completed sessions would be the whole failure.
class BookingsScreen extends StatefulWidget {
  const BookingsScreen({super.key});

  @override
  State<BookingsScreen> createState() => _BookingsScreenState();
}

class _BookingsScreenState extends State<BookingsScreen> {
  late Future<List<BookingSummary>> _bookings;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _bookings = Services.of(context).myBookings();
  }

  Future<void> _reload() async {
    setState(() => _bookings = Services.of(context).myBookings());
    await _bookings;
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('My bookings'),
        actions: const [NotificationsBell(), HomeMenuButton()],
      ),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<List<BookingSummary>>(
          future: _bookings,
          builder: (context, snapshot) => AsyncView<List<BookingSummary>>(
            snapshot: snapshot,
            onRetry: _reload,
            isEmpty: (data) => data.isEmpty,
            empty: ListView(
              children: [
                const SizedBox(height: 80),
                EmptyState(
                  icon: Icons.event_available_outlined,
                  title: 'No bookings yet',
                  message: 'Find a space near you and reserve it by the slot.',
                  action: FilledButton(
                    onPressed: () => context.go('/explore'),
                    child: const Text('Find parking'),
                  ),
                ),
              ],
            ),
            builder: (bookings) {
              final open = bookings.where((b) => b.isOpen).toList();
              final past = bookings.where((b) => !b.isOpen).toList();

              return ListView(
                padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                children: [
                  if (open.isNotEmpty) ...[
                    _Heading('Open', count: open.length),
                    for (final booking in open) _BookingCard(booking: booking, onChanged: _reload),
                  ],
                  if (past.isNotEmpty) ...[
                    _Heading('Past', count: past.length),
                    for (final booking in past) _BookingCard(booking: booking, onChanged: _reload),
                  ],
                ],
              );
            },
          ),
        ),
      ),
    );
  }
}

class _Heading extends StatelessWidget {
  const _Heading(this.label, {required this.count});

  final String label;
  final int count;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(4, 16, 4, 8),
      child: Text(
        '$label · $count',
        style: Theme.of(context).textTheme.titleSmall?.copyWith(
              color: Theme.of(context).colorScheme.onSurfaceVariant,
            ),
      ),
    );
  }
}

class _BookingCard extends StatelessWidget {
  const _BookingCard({required this.booking, required this.onChanged});

  final BookingSummary booking;
  final Future<void> Function() onChanged;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    // An active session past its expected end is billing an overstay right now. That is the one
    // state on this card worth colouring, because the cost is still climbing.
    final overdue = booking.status == 'Active' && booking.expectedEndTime.isBefore(DateTime.now());

    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Card(
        child: InkWell(
          borderRadius: BorderRadius.circular(16),
          onTap: () async {
            await context.push('/bookings/${booking.id}');
            await onChanged();
          },
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        booking.spaceTitle,
                        style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 16),
                        overflow: TextOverflow.ellipsis,
                      ),
                    ),
                    const SizedBox(width: 8),
                    StatusChip(booking.status),
                  ],
                ),
                const SizedBox(height: 4),
                Text(
                  booking.spaceAddress,
                  style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
                const SizedBox(height: 12),
                Row(
                  children: [
                    Icon(Icons.schedule, size: 16, color: scheme.onSurfaceVariant),
                    const SizedBox(width: 6),
                    Expanded(
                      child: Text(
                        '${formatDateTime(booking.startTime)} → '
                        '${formatTime(booking.expectedEndTime)}',
                        style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                      ),
                    ),
                    Text(
                      booking.isOpen
                          ? formatCredits(booking.holdAmount)
                          : formatCredits(booking.settledAmount),
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                  ],
                ),
                if (booking.status == 'Active') ...[
                  const SizedBox(height: 8),
                  Text(
                    formatRemaining(booking.expectedEndTime),
                    style: TextStyle(
                      color: overdue ? scheme.error : scheme.primary,
                      fontWeight: FontWeight.w600,
                      fontSize: 13,
                    ),
                  ),
                ],
              ],
            ),
          ),
        ),
      ),
    );
  }
}

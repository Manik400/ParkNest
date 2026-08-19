import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import 'home_shell.dart';

/// The host side: your listings, and what has been booked against them.
///
/// One account is both sides — the API authorises per action rather than locking anyone into a
/// role — so this is a tab rather than a separate app or a mode switch. Someone with no listings
/// gets told how to start rather than an empty screen.
class HostingScreen extends StatefulWidget {
  const HostingScreen({super.key});

  @override
  State<HostingScreen> createState() => _HostingScreenState();
}

class _HostingScreenState extends State<HostingScreen> {
  late Future<({List<ListingSummary> listings, List<BookingSummary> bookings})> _data;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    final api = Services.of(context);

    setState(() {
      _data = Future.wait([api.myListings(), api.hostingBookings()]).then(
        (results) => (
          listings: results[0] as List<ListingSummary>,
          bookings: results[1] as List<BookingSummary>,
        ),
      );
    });
  }

  Future<void> _reload() async {
    _load();
    await _data;
  }

  Future<void> _addListing() async {
    final created = await context.push<bool>('/listings/new');

    // Only reload when something was actually created; backing out of the form should not make
    // the list flash.
    if ((created ?? false) && mounted) _load();
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Hosting'), actions: const [HomeMenuButton()]),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _addListing,
        icon: const Icon(Icons.add),
        label: const Text('List a space'),
      ),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<({List<ListingSummary> listings, List<BookingSummary> bookings})>(
          future: _data,
          builder: (context, snapshot) =>
              AsyncView<({List<ListingSummary> listings, List<BookingSummary> bookings})>(
            snapshot: snapshot,
            onRetry: _reload,
            builder: (data) => _build(data.listings, data.bookings),
          ),
        ),
      ),
    );
  }

  Widget _build(List<ListingSummary> listings, List<BookingSummary> bookings) {
    final scheme = Theme.of(context).colorScheme;

    if (listings.isEmpty) {
      return ListView(
        children: [
          const SizedBox(height: 60),
          EmptyState(
            icon: Icons.home_work_outlined,
            title: 'You are not hosting yet',
            message: 'Rent out a driveway, a basement bay, or a spot you are not using.',
            action: FilledButton(
              onPressed: _addListing,
              child: const Text('List a space'),
            ),
          ),
        ],
      );
    }

    final open = bookings.where((b) => b.isOpen).toList();

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
      children: [
        Text('Your spaces', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),
        for (final listing in listings)
          Padding(
            padding: const EdgeInsets.only(bottom: 12),
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(16),
                child: Row(
                  children: [
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            listing.title,
                            style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 16),
                            overflow: TextOverflow.ellipsis,
                          ),
                          const SizedBox(height: 2),
                          Text(
                            listing.addressLine,
                            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                            maxLines: 1,
                            overflow: TextOverflow.ellipsis,
                          ),
                          const SizedBox(height: 8),
                          Text(
                            '${formatCredits(listing.pricePerHour)} / hr'
                            '${listing.activeBookings > 0 ? ' · ${listing.activeBookings} booked now' : ''}',
                            style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                          ),
                        ],
                      ),
                    ),
                    const SizedBox(width: 8),
                    StatusChip(listing.status),
                  ],
                ),
              ),
            ),
          ),

        const SizedBox(height: 16),
        Text('Bookings on your spaces', style: Theme.of(context).textTheme.titleMedium),
        const SizedBox(height: 8),

        if (bookings.isEmpty)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 16),
            child: Text(
              'Nothing booked yet.',
              style: TextStyle(color: scheme.onSurfaceVariant),
            ),
          )
        else
          for (final booking in [...open, ...bookings.where((b) => !b.isOpen)])
            ListTile(
              contentPadding: EdgeInsets.zero,
              onTap: () => context.push('/bookings/${booking.id}'),
              title: Text(booking.spaceTitle),
              subtitle: Text(
                '${formatDateTime(booking.startTime)} · ${formatTime(booking.expectedEndTime)}',
              ),
              trailing: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                crossAxisAlignment: CrossAxisAlignment.end,
                children: [
                  StatusChip(booking.status),
                  const SizedBox(height: 4),
                  Text(
                    // Before settlement the host's share is not decided yet, so showing the hold
                    // would imply an income they may not receive. The reserved figure is the
                    // honest number until the session ends.
                    booking.isOpen
                        ? '${formatCredits(booking.holdAmount)} reserved'
                        : formatCredits(booking.settledAmount),
                    style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                  ),
                ],
              ),
            ),
      ],
    );
  }
}

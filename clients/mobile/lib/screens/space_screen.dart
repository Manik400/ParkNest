import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import '../widgets/listing_photos.dart';

/// Quote a session, then book it.
///
/// Nothing is reserved until the last button. The quote endpoint prices the session and reports
/// whether it *could* be booked — outside the host's availability, below the minimum duration, a
/// clash — so the reason a booking would be refused is shown before the renter commits rather than
/// as an error after.
class SpaceScreen extends StatefulWidget {
  const SpaceScreen({required this.spaceId, required this.title, super.key});

  final String spaceId;
  final String title;

  @override
  State<SpaceScreen> createState() => _SpaceScreenState();
}

class _SpaceScreenState extends State<SpaceScreen> {
  static const _durations = [30, 60, 120, 240, 480];

  late DateTime _start;
  int _durationMinutes = 60;

  Future<List<Vehicle>>? _vehicles;
  Vehicle? _vehicle;

  Future<BookingQuote>? _quote;
  Future<List<ListingPhoto>>? _photos;
  bool _booking = false;

  @override
  void initState() {
    super.initState();

    // Rounded up to the next five minutes. The API refuses a start more than five minutes in the
    // past, and "now" by the time the user has picked a vehicle is often already behind.
    final now = DateTime.now();
    _start = DateTime(now.year, now.month, now.day, now.hour, (now.minute ~/ 5) * 5 + 5);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();

    _vehicles ??= Services.of(context).myVehicles().then((vehicles) {
      if (mounted && vehicles.isNotEmpty) {
        setState(() => _vehicle = vehicles.first);
      }
      return vehicles;
    });

    _quote ??= _requestQuote();

    // Failing softly on purpose: a listing with no photographs, or a deployment with storage
    // switched off, must still be bookable. The strip simply does not appear.
    _photos ??= Services.of(context)
        .listingPhotos(widget.spaceId)
        .catchError((_) => <ListingPhoto>[]);
  }

  Future<BookingQuote> _requestQuote() => Services.of(context).quote(
        spaceId: widget.spaceId,
        startTime: _start,
        durationMinutes: _durationMinutes,
      );

  void _requote() => setState(() => _quote = _requestQuote());

  Future<void> _pickStart() async {
    final date = await showDatePicker(
      context: context,
      initialDate: _start,
      firstDate: DateTime.now().subtract(const Duration(minutes: 5)),
      lastDate: DateTime.now().add(const Duration(days: 30)),
    );

    if (date == null || !mounted) return;

    final time = await showTimePicker(
      context: context,
      initialTime: TimeOfDay.fromDateTime(_start),
    );

    if (time == null || !mounted) return;

    setState(() => _start = DateTime(date.year, date.month, date.day, time.hour, time.minute));
    _requote();
  }

  Future<void> _book(BookingQuote quote) async {
    final vehicle = _vehicle;

    if (vehicle == null) {
      showError(context, 'Add a vehicle before booking.');
      return;
    }

    setState(() => _booking = true);

    try {
      final bookingId = await Services.of(context).book(
        parkingSpaceId: widget.spaceId,
        vehicleId: vehicle.id,
        startTime: _start,
        durationMinutes: _durationMinutes,
        // Derived from the request, not random: a retry after a timeout must land on the same
        // booking rather than reserving the credits twice.
        idempotencyKey: 'book:${widget.spaceId}:${vehicle.id}:'
            '${_start.toUtc().toIso8601String()}:$_durationMinutes',
      );

      if (!mounted) return;

      context.pushReplacement('/bookings/$bookingId');
    } on ApiException catch (error) {
      if (!mounted) return;

      setState(() => _booking = false);

      if (error.isInsufficientCredits) {
        // 402 has a specific remedy, so offer it instead of just reporting the failure.
        final topUp = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Not enough credits'),
            content: Text(error.message),
            actions: [
              TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Later')),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('Add credits'),
              ),
            ],
          ),
        );

        if ((topUp ?? false) && mounted) context.go('/wallet');
      } else {
        showError(context, error);
      }
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: Text(widget.title)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 32),
        children: [
          FutureBuilder<List<ListingPhoto>>(
            future: _photos,
            builder: (context, snapshot) {
              final photos = snapshot.data ?? const <ListingPhoto>[];

              return photos.isEmpty
                  ? const SizedBox.shrink()
                  : Padding(
                      padding: const EdgeInsets.only(bottom: 16),
                      child: ListingPhotoStrip(photos: photos),
                    );
            },
          ),
          SectionCard(
            title: 'When',
            child: Column(
              children: [
                ListTile(
                  contentPadding: EdgeInsets.zero,
                  leading: const Icon(Icons.schedule),
                  title: const Text('Start'),
                  subtitle: Text(formatDateTime(_start)),
                  trailing: const Icon(Icons.edit_outlined),
                  onTap: _pickStart,
                ),
                const SizedBox(height: 8),
                Align(
                  alignment: Alignment.centerLeft,
                  child: Text('For how long',
                      style: TextStyle(color: Theme.of(context).colorScheme.onSurfaceVariant)),
                ),
                const SizedBox(height: 8),
                Wrap(
                  spacing: 8,
                  children: [
                    for (final minutes in _durations)
                      ChoiceChip(
                        label: Text(formatDuration(minutes)),
                        selected: _durationMinutes == minutes,
                        onSelected: (_) {
                          setState(() => _durationMinutes = minutes);
                          _requote();
                        },
                      ),
                  ],
                ),
              ],
            ),
          ),
          const SizedBox(height: 16),

          SectionCard(
            title: 'Vehicle',
            child: FutureBuilder<List<Vehicle>>(
              future: _vehicles,
              builder: (context, snapshot) {
                final vehicles = snapshot.data;

                if (vehicles == null) {
                  return const LinearProgressIndicator();
                }

                if (vehicles.isEmpty) {
                  return Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text('No vehicles yet. A booking needs one.'),
                      const SizedBox(height: 12),
                      OutlinedButton(
                        onPressed: () async {
                          await context.push('/vehicles');
                          if (mounted) {
                            setState(() => _vehicles = Services.of(context).myVehicles());
                          }
                        },
                        child: const Text('Add a vehicle'),
                      ),
                    ],
                  );
                }

                return Column(
                  children: [
                    for (final vehicle in vehicles)
                      RadioListTile<String>(
                        contentPadding: EdgeInsets.zero,
                        value: vehicle.id,
                        groupValue: _vehicle?.id,
                        onChanged: (_) => setState(() => _vehicle = vehicle),
                        title: Text(vehicle.plateNumber),
                        subtitle: Text(humaniseType(vehicle.type)),
                      ),
                  ],
                );
              },
            ),
          ),
          const SizedBox(height: 16),

          FutureBuilder<BookingQuote>(
            future: _quote,
            builder: (context, snapshot) => AsyncView<BookingQuote>(
              snapshot: snapshot,
              onRetry: _requote,
              builder: _buildQuote,
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildQuote(BookingQuote quote) {
    final scheme = Theme.of(context).colorScheme;

    return Column(
      children: [
        SectionCard(
          title: 'Cost',
          child: Column(
            children: [
              FactRow('Rate', '${formatCredits(quote.ratePerHour)} / hr'),
              FactRow('Billed', formatDuration(quote.billedMinutes)),
              FactRow('Reserved now', formatCredits(quote.amount), emphasised: true),
              const SizedBox(height: 8),
              // Stated up front on purpose. The overstay rate is the part of this model that
              // surprises people, and a renter who only meets it at checkout is right to feel
              // ambushed.
              Text(
                'If you stay past the end, the extra bills at '
                '${formatCredits(quote.overstayRatePerHour)} / hr from your balance.',
                style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),

        if (!quote.canBook)
          Card(
            color: scheme.errorContainer,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                quote.unavailable ?? 'This space cannot be booked then.',
                style: TextStyle(color: scheme.onErrorContainer),
              ),
            ),
          )
        else
          FilledButton(
            onPressed: _booking || _vehicle == null ? null : () => _book(quote),
            child: _booking
                ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2))
                : Text('Reserve for ${formatCredits(quote.amount)}'),
          ),
      ],
    );
  }

  static String humaniseType(String type) => switch (type) {
        'TwoWheeler' => 'Two-wheeler',
        'FourWheeler' => 'Four-wheeler',
        _ => type,
      };
}

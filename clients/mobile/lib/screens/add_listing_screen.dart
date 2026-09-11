import 'package:flutter/material.dart';
import 'package:latlong2/latlong.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/location.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import '../widgets/space_map.dart';

/// List a space from the phone.
///
/// Two steps rather than one form, because they ask different things of the host: what the space
/// is, and where it is. The pin is the part that cannot be typed accurately, so it gets a whole
/// screen and starts from the host's own location — someone listing their driveway is usually
/// standing in it.
class AddListingScreen extends StatefulWidget {
  const AddListingScreen({super.key});

  @override
  State<AddListingScreen> createState() => _AddListingScreenState();
}

class _AddListingScreenState extends State<AddListingScreen> {
  static const _fallback = LatLng(12.9716, 77.5946);
  static const _location = LocationService();

  final _form = GlobalKey<FormState>();
  final _title = TextEditingController();
  final _address = TextEditingController();
  final _city = TextEditingController(text: 'Bengaluru');
  final _price = TextEditingController(text: '60');

  LatLng _point = _fallback;
  bool _locating = true;

  final _vehicleTypes = <String>{'FourWheeler'};
  bool _openAllHours = true;
  TimeOfDay _opensAt = const TimeOfDay(hour: 8, minute: 0);
  TimeOfDay _closesAt = const TimeOfDay(hour: 20, minute: 0);

  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _locate();
  }

  @override
  void dispose() {
    _title.dispose();
    _address.dispose();
    _city.dispose();
    _price.dispose();
    super.dispose();
  }

  Future<void> _locate() async {
    final result = await _location.current();

    if (!mounted) return;

    setState(() {
      if (result.isSuccess) {
        _point = LatLng(result.latitude!, result.longitude!);
      }
      _locating = false;
    });
  }

  List<AvailabilityWindowRequest> _windows() {
    const days = [
      'Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday',
    ];

    // Equal start and end means the full twenty-four hours — that is the API's convention, and it
    // is why "always open" is not expressed by omitting the windows.
    final start = _openAllHours ? Duration.zero : Duration(hours: _opensAt.hour, minutes: _opensAt.minute);
    final end = _openAllHours ? Duration.zero : Duration(hours: _closesAt.hour, minutes: _closesAt.minute);

    return [
      for (final day in days)
        AvailabilityWindowRequest(dayOfWeek: day, startTime: start, endTime: end),
    ];
  }

  Future<void> _submit({required bool publish}) async {
    if (!(_form.currentState?.validate() ?? false)) return;

    if (_vehicleTypes.isEmpty) {
      showError(context, 'Pick at least one vehicle type this space fits.');
      return;
    }

    final api = Services.of(context);
    // Captured before the pop: afterwards this widget's context is on its way out, and the
    // confirmation belongs to the screen underneath anyway.
    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);

    setState(() => _busy = true);

    try {
      final spaceId = await api.createListing(CreateListingRequest(
        title: _title.text.trim(),
        addressLine: _address.text.trim(),
        city: _city.text.trim(),
        latitude: _point.latitude,
        longitude: _point.longitude,
        pricePerHour: double.parse(_price.text.trim()),
        supportedVehicleTypes: _vehicleTypes.toList(),
        availabilityWindows: _windows(),
      ));

      if (publish) {
        // Separate call because publishing is what validates the price against the city band. A
        // draft that saved and then failed to publish is still the host's work, not a lost form —
        // so this reports the reason and leaves the listing to be published later.
        await api.publishListing(spaceId);
      }

      if (!mounted) return;

      navigator.pop(true);
      messenger.showSnackBar(SnackBar(
        content: Text(publish ? 'Space published.' : 'Saved as a draft.'),
        behavior: SnackBarBehavior.floating,
      ));
    } on ApiException catch (error) {
      if (!mounted) return;

      setState(() => _busy = false);
      showError(context, error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('List a space')),
      body: Form(
        key: _form,
        child: ListView(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 32),
          children: [
            SectionCard(
              title: 'Where it is',
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Text(
                    'Tap the map to move the pin. Renters navigate to this point, so put it on the '
                    'entrance rather than the middle of the building.',
                    style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                  ),
                  const SizedBox(height: 12),
                  ClipRRect(
                    borderRadius: BorderRadius.circular(12),
                    child: SizedBox(
                      height: 260,
                      child: _locating
                          ? const Center(child: CircularProgressIndicator())
                          : PointPicker(
                              point: _point,
                              onChanged: (point) => setState(() => _point = point),
                            ),
                    ),
                  ),
                  const SizedBox(height: 8),
                  Row(
                    children: [
                      Expanded(
                        child: Text(
                          '${_point.latitude.toStringAsFixed(5)}, '
                          '${_point.longitude.toStringAsFixed(5)}',
                          style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                        ),
                      ),
                      TextButton.icon(
                        onPressed: _locating ? null : _locate,
                        icon: const Icon(Icons.my_location, size: 18),
                        label: const Text('Use my location'),
                      ),
                    ],
                  ),
                ],
              ),
            ),
            const SizedBox(height: 16),

            SectionCard(
              title: 'What it is',
              child: Column(
                children: [
                  TextFormField(
                    controller: _title,
                    textCapitalization: TextCapitalization.sentences,
                    decoration: const InputDecoration(
                      labelText: 'Title',
                      hintText: 'Covered driveway, gate 2',
                    ),
                    validator: (value) =>
                        (value ?? '').trim().isEmpty ? 'Give the space a name renters will recognise.' : null,
                  ),
                  const SizedBox(height: 12),
                  TextFormField(
                    controller: _address,
                    textCapitalization: TextCapitalization.words,
                    decoration: const InputDecoration(labelText: 'Address'),
                    validator: (value) =>
                        (value ?? '').trim().isEmpty ? 'An address is required.' : null,
                  ),
                  const SizedBox(height: 12),
                  TextFormField(
                    controller: _city,
                    textCapitalization: TextCapitalization.words,
                    decoration: const InputDecoration(labelText: 'City'),
                    // The city selects the price band. Getting it wrong is not a typo, it is a
                    // listing that cannot be published at all.
                    validator: (value) =>
                        (value ?? '').trim().isEmpty ? 'The city decides which price band applies.' : null,
                  ),
                ],
              ),
            ),
            const SizedBox(height: 16),

            SectionCard(
              title: 'What fits',
              child: Wrap(
                spacing: 8,
                children: [
                  for (final entry in const {
                    'FourWheeler': 'Car',
                    'TwoWheeler': 'Two-wheeler',
                  }.entries)
                    FilterChip(
                      label: Text(entry.value),
                      selected: _vehicleTypes.contains(entry.key),
                      onSelected: (selected) => setState(() {
                        if (selected) {
                          _vehicleTypes.add(entry.key);
                        } else {
                          _vehicleTypes.remove(entry.key);
                        }
                      }),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 16),

            SectionCard(
              title: 'When it is open',
              child: Column(
                children: [
                  SwitchListTile(
                    contentPadding: EdgeInsets.zero,
                    value: _openAllHours,
                    onChanged: (value) => setState(() => _openAllHours = value),
                    title: const Text('Open all hours'),
                    subtitle: const Text('Every day, around the clock'),
                  ),
                  if (!_openAllHours) ...[
                    const Divider(),
                    _TimeRow(
                      label: 'Opens',
                      time: _opensAt,
                      onChanged: (time) => setState(() => _opensAt = time),
                    ),
                    _TimeRow(
                      label: 'Closes',
                      time: _closesAt,
                      onChanged: (time) => setState(() => _closesAt = time),
                    ),
                    if (_closesAt.hour < _opensAt.hour)
                      Padding(
                        padding: const EdgeInsets.only(top: 8),
                        child: Text(
                          'Closing before it opens is read as running overnight.',
                          style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                        ),
                      ),
                  ],
                ],
              ),
            ),
            const SizedBox(height: 16),

            SectionCard(
              title: 'What it costs',
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  TextFormField(
                    controller: _price,
                    keyboardType: const TextInputType.numberWithOptions(decimal: true),
                    decoration: const InputDecoration(labelText: 'Per hour', prefixText: '₹ '),
                    validator: (value) {
                      final parsed = double.tryParse((value ?? '').trim());
                      return parsed == null || parsed <= 0 ? 'Enter an hourly rate.' : null;
                    },
                  ),
                  const SizedBox(height: 8),
                  // The band is enforced server-side at publish, and the message names the range.
                  // Repeating a guess here would just be a second number to keep in sync.
                  Text(
                    'Your city sets a minimum and maximum. Publishing tells you if this falls '
                    'outside it.',
                    style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 24),

            FilledButton(
              onPressed: _busy ? null : () => _submit(publish: true),
              child: _busy
                  ? const SizedBox(height: 20, width: 20, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Text('Publish'),
            ),
            const SizedBox(height: 8),
            OutlinedButton(
              onPressed: _busy ? null : () => _submit(publish: false),
              child: const Text('Save as draft'),
            ),
          ],
        ),
      ),
    );
  }
}

class _TimeRow extends StatelessWidget {
  const _TimeRow({required this.label, required this.time, required this.onChanged});

  final String label;
  final TimeOfDay time;
  final ValueChanged<TimeOfDay> onChanged;

  @override
  Widget build(BuildContext context) {
    return ListTile(
      contentPadding: EdgeInsets.zero,
      title: Text(label),
      trailing: Text(
        formatTime(DateTime(2026, 1, 1, time.hour, time.minute)),
        style: const TextStyle(fontWeight: FontWeight.w600),
      ),
      onTap: () async {
        final picked = await showTimePicker(context: context, initialTime: time);
        if (picked != null) onChanged(picked);
      },
    );
  }
}

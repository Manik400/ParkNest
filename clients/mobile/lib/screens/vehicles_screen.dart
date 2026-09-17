import 'package:flutter/material.dart';

import '../core/api_client.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// The plates the account can book with.
///
/// Kept as its own screen rather than a field on the booking form because a plate is stable and a
/// booking is not — and in Tier 3 this list becomes what ANPR matches an arriving car against.
class VehiclesScreen extends StatefulWidget {
  const VehiclesScreen({super.key});

  @override
  State<VehiclesScreen> createState() => _VehiclesScreenState();
}

class _VehiclesScreenState extends State<VehiclesScreen> {
  late Future<List<Vehicle>> _vehicles;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _vehicles = Services.of(context).myVehicles();
  }

  void _reload() => setState(() => _vehicles = Services.of(context).myVehicles());

  Future<void> _add() async {
    // Captured before the sheet: reading it back after the await would use a context that may
    // already be gone.
    final api = Services.of(context);

    final result = await showModalBottomSheet<({String plate, String type})>(
      context: context,
      isScrollControlled: true,
      builder: (_) => const _AddVehicleSheet(),
    );

    if (result == null) return;

    try {
      await api.addVehicle(result.plate, result.type);
      if (mounted) _reload();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    }
  }

  Future<void> _remove(Vehicle vehicle) async {
    try {
      await Services.of(context).removeVehicle(vehicle.id);
      if (mounted) _reload();
    } on ApiException catch (error) {
      // The API refuses to remove a vehicle with a live booking against it, and says so. Passing
      // that through is more use than a generic failure.
      if (mounted) showError(context, error);
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('My vehicles')),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: _add,
        icon: const Icon(Icons.add),
        label: const Text('Add'),
      ),
      body: FutureBuilder<List<Vehicle>>(
        future: _vehicles,
        builder: (context, snapshot) => AsyncView<List<Vehicle>>(
          snapshot: snapshot,
          onRetry: _reload,
          isEmpty: (data) => data.isEmpty,
          empty: const EmptyState(
            icon: Icons.directions_car_outlined,
            title: 'No vehicles',
            message: 'Add the plate you will park with.',
          ),
          builder: (vehicles) => ListView.separated(
            padding: const EdgeInsets.symmetric(vertical: 8),
            itemCount: vehicles.length,
            separatorBuilder: (_, __) => const Divider(height: 1),
            itemBuilder: (_, i) {
              final vehicle = vehicles[i];

              return ListTile(
                leading: Icon(
                  vehicle.type == 'TwoWheeler' ? Icons.two_wheeler : Icons.directions_car,
                ),
                title: Text(vehicle.plateNumber),
                subtitle: Text(_humanise(vehicle.type)),
                trailing: IconButton(
                  icon: const Icon(Icons.delete_outline),
                  onPressed: () => _remove(vehicle),
                ),
              );
            },
          ),
        ),
      ),
    );
  }

  static String _humanise(String type) => switch (type) {
        'TwoWheeler' => 'Two-wheeler',
        'FourWheeler' => 'Four-wheeler',
        _ => type,
      };
}

class _AddVehicleSheet extends StatefulWidget {
  const _AddVehicleSheet();

  @override
  State<_AddVehicleSheet> createState() => _AddVehicleSheetState();
}

class _AddVehicleSheetState extends State<_AddVehicleSheet> {
  final _plate = TextEditingController();
  String _type = 'FourWheeler';

  @override
  void dispose() {
    _plate.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
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
          Text('Add a vehicle', style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 16),
          TextField(
            controller: _plate,
            autofocus: true,
            textCapitalization: TextCapitalization.characters,
            onChanged: (_) => setState(() {}),
            decoration: const InputDecoration(labelText: 'Plate number', hintText: 'KA01AB1234'),
          ),
          const SizedBox(height: 16),
          SegmentedButton<String>(
            segments: const [
              ButtonSegment(value: 'FourWheeler', label: Text('Car'), icon: Icon(Icons.directions_car)),
              ButtonSegment(value: 'TwoWheeler', label: Text('Two-wheeler'), icon: Icon(Icons.two_wheeler)),
            ],
            selected: {_type},
            onSelectionChanged: (selection) => setState(() => _type = selection.first),
          ),
          const SizedBox(height: 20),
          FilledButton(
            onPressed: _plate.text.trim().isEmpty
                ? null
                // The API normalises the plate and rejects a duplicate; this only avoids an
                // obviously empty submission.
                : () => Navigator.pop(context, (plate: _plate.text.trim(), type: _type)),
            child: const Text('Add vehicle'),
          ),
        ],
      ),
    );
  }
}

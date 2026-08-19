import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:latlong2/latlong.dart';

import '../core/formatting.dart';
import '../core/location.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import '../widgets/space_map.dart';
import 'home_shell.dart';

/// Find a space near you.
///
/// Opens on the device's location and searches from it. When that is unavailable — permission
/// refused, GPS off, no fix indoors — it falls back to a fixed point and says so, because a search
/// screen that shows nothing and explains nothing is worse than one searching the wrong place.
class ExploreScreen extends StatefulWidget {
  const ExploreScreen({super.key});

  @override
  State<ExploreScreen> createState() => _ExploreScreenState();
}

class _ExploreScreenState extends State<ExploreScreen> {
  /// Bengaluru city centre, used only when the device will not say where it is.
  static const _fallback = LatLng(12.9716, 77.5946);

  static const _location = LocationService();

  LatLng _centre = _fallback;
  bool _usingFallback = true;
  LocationResult? _locationFailure;

  int _radiusMetres = 2000;
  double? _maxPrice;
  bool _showMap = true;

  NearbySpace? _selected;
  Future<List<NearbySpace>>? _results;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _locate());
  }

  Future<void> _locate() async {
    final result = await _location.current();

    if (!mounted) return;

    setState(() {
      if (result.isSuccess) {
        _centre = LatLng(result.latitude!, result.longitude!);
        _usingFallback = false;
        _locationFailure = null;
      } else {
        // Keep whatever centre we had and search anyway. Refusing to search until location is
        // granted would make the permission dialog a gate on seeing the app work at all.
        _locationFailure = result;
      }
    });

    _search();
  }

  void _search() {
    setState(() {
      _selected = null;
      _results = Services.of(context).searchNearby(
        latitude: _centre.latitude,
        longitude: _centre.longitude,
        radiusMetres: _radiusMetres,
        maxPricePerHour: _maxPrice,
      );
    });
  }

  void _open(NearbySpace space) => context.push(
        '/spaces/${space.id}?title=${Uri.encodeQueryComponent(space.title)}',
      );

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Find parking'),
        actions: [
          IconButton(
            tooltip: _showMap ? 'Show as a list' : 'Show on a map',
            icon: Icon(_showMap ? Icons.view_list_outlined : Icons.map_outlined),
            onPressed: () => setState(() => _showMap = !_showMap),
          ),
          const HomeMenuButton(),
        ],
      ),
      floatingActionButton: FloatingActionButton.small(
        onPressed: _locate,
        tooltip: 'Search from my location',
        child: Icon(_usingFallback ? Icons.location_searching : Icons.my_location),
      ),
      body: Column(
        children: [
          if (_locationFailure case final failure?) _LocationBanner(failure: failure, onRetry: _locate),
          _Filters(
            radiusMetres: _radiusMetres,
            maxPrice: _maxPrice,
            onRadiusChanged: (value) {
              setState(() => _radiusMetres = value);
              _search();
            },
            onMaxPriceChanged: (value) {
              setState(() => _maxPrice = value);
              _search();
            },
          ),
          Expanded(
            child: FutureBuilder<List<NearbySpace>>(
              future: _results,
              builder: (context, snapshot) => AsyncView<List<NearbySpace>>(
                snapshot: snapshot,
                onRetry: _search,
                isEmpty: (data) => data.isEmpty && !_showMap,
                empty: const EmptyState(
                  icon: Icons.location_off_outlined,
                  title: 'Nothing published nearby',
                  message: 'Widen the radius, or raise the price ceiling.',
                ),
                builder: (spaces) => _showMap ? _buildMap(spaces) : _buildList(spaces),
              ),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildMap(List<NearbySpace> spaces) {
    return Stack(
      children: [
        SpaceMap(
          centre: _centre,
          spaces: spaces,
          selected: _selected,
          onSelected: (space) => setState(() => _selected = space),
        ),

        if (spaces.isEmpty)
          Positioned(
            left: 16,
            right: 16,
            top: 16,
            child: Card(
              child: Padding(
                padding: const EdgeInsets.all(12),
                child: Text(
                  'No spaces published within ${formatDistance(_radiusMetres.toDouble())}.',
                  textAlign: TextAlign.center,
                ),
              ),
            ),
          ),

        // The selected pin's details, docked. Keeps the map visible while deciding, which is the
        // point of being on a map at all.
        if (_selected case final space?)
          Positioned(
            left: 12,
            right: 12,
            bottom: 12,
            child: Card(
              elevation: 3,
              child: InkWell(
                borderRadius: BorderRadius.circular(16),
                onTap: () => _open(space),
                child: Padding(
                  padding: const EdgeInsets.all(16),
                  child: Row(
                    children: [
                      Expanded(child: _SpaceSummary(space: space)),
                      const Icon(Icons.chevron_right),
                    ],
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }

  Widget _buildList(List<NearbySpace> spaces) => ListView.separated(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
        itemCount: spaces.length,
        separatorBuilder: (_, __) => const SizedBox(height: 12),
        itemBuilder: (_, i) => Card(
          child: InkWell(
            borderRadius: BorderRadius.circular(16),
            onTap: () => _open(spaces[i]),
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: _SpaceSummary(space: spaces[i]),
            ),
          ),
        ),
      );
}

class _LocationBanner extends StatelessWidget {
  const _LocationBanner({required this.failure, required this.onRetry});

  final LocationResult failure;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Container(
      width: double.infinity,
      color: scheme.secondaryContainer,
      padding: const EdgeInsets.fromLTRB(16, 10, 8, 10),
      child: Row(
        children: [
          Expanded(
            child: Text(
              '${failure.message} Showing results for the city centre instead.',
              style: TextStyle(color: scheme.onSecondaryContainer, fontSize: 13),
            ),
          ),
          // Retrying is only worth offering when a retry can succeed. Once it is denied forever,
          // the only thing that helps is system settings.
          TextButton(
            onPressed: failure.isRecoverableInApp
                ? onRetry
                : () => const LocationService().openSettings(),
            child: Text(failure.isRecoverableInApp ? 'Retry' : 'Settings'),
          ),
        ],
      ),
    );
  }
}

class _Filters extends StatelessWidget {
  const _Filters({
    required this.radiusMetres,
    required this.maxPrice,
    required this.onRadiusChanged,
    required this.onMaxPriceChanged,
  });

  final int radiusMetres;
  final double? maxPrice;
  final ValueChanged<int> onRadiusChanged;
  final ValueChanged<double?> onMaxPriceChanged;

  @override
  Widget build(BuildContext context) {
    return SingleChildScrollView(
      scrollDirection: Axis.horizontal,
      padding: const EdgeInsets.fromLTRB(16, 4, 16, 8),
      child: Row(
        children: [
          for (final radius in [1000, 2000, 5000])
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: ChoiceChip(
                label: Text(formatDistance(radius.toDouble())),
                selected: radiusMetres == radius,
                onSelected: (_) => onRadiusChanged(radius),
              ),
            ),
          const SizedBox(width: 4),
          for (final price in [50.0, 100.0])
            Padding(
              padding: const EdgeInsets.only(right: 8),
              child: FilterChip(
                label: Text('under ${formatCredits(price)}'),
                selected: maxPrice == price,
                onSelected: (selected) => onMaxPriceChanged(selected ? price : null),
              ),
            ),
        ],
      ),
    );
  }
}

class _SpaceSummary extends StatelessWidget {
  const _SpaceSummary({required this.space});

  final NearbySpace space;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Row(
      children: [
        Expanded(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                space.title,
                style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 16),
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 2),
              Text(
                space.addressLine,
                style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
              ),
              const SizedBox(height: 8),
              Row(
                children: [
                  Icon(Icons.near_me_outlined, size: 14, color: scheme.onSurfaceVariant),
                  const SizedBox(width: 4),
                  Text(
                    formatDistance(space.distanceMetres),
                    style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
                  ),
                ],
              ),
            ],
          ),
        ),
        const SizedBox(width: 12),
        Column(
          crossAxisAlignment: CrossAxisAlignment.end,
          children: [
            Text(
              formatCredits(space.pricePerHour),
              style: const TextStyle(fontWeight: FontWeight.w700, fontSize: 16),
            ),
            Text('per hour', style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12)),
          ],
        ),
      ],
    );
  }
}

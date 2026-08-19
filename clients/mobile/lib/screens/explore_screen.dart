import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import 'home_shell.dart';

/// Find a space near a point.
///
/// A list, not a map. The API returns real geodesic distance and sorts by it, so a list is already
/// ordered by the thing that matters — and a map on this screen would be a large dependency to
/// carry before there is any location permission flow to feed it. That is the honest sequencing:
/// device location, then a map worth looking at.
class ExploreScreen extends StatefulWidget {
  const ExploreScreen({super.key});

  @override
  State<ExploreScreen> createState() => _ExploreScreenState();
}

class _ExploreScreenState extends State<ExploreScreen> {
  // Bengaluru city centre. A stand-in until device location lands — better than an empty screen
  // that cannot demonstrate anything.
  static const _defaultLat = 12.9716;
  static const _defaultLng = 77.5946;

  final _lat = TextEditingController(text: '$_defaultLat');
  final _lng = TextEditingController(text: '$_defaultLng');

  int _radiusMetres = 2000;
  double? _maxPrice;

  Future<List<NearbySpace>>? _results;

  @override
  void initState() {
    super.initState();
    // Search straight away rather than making the first action a button press against defaults
    // the user did not choose.
    WidgetsBinding.instance.addPostFrameCallback((_) => _search());
  }

  @override
  void dispose() {
    _lat.dispose();
    _lng.dispose();
    super.dispose();
  }

  void _search() {
    final latitude = double.tryParse(_lat.text.trim());
    final longitude = double.tryParse(_lng.text.trim());

    if (latitude == null || longitude == null) {
      showError(context, 'That does not look like a coordinate.');
      return;
    }

    setState(() {
      _results = Services.of(context).searchNearby(
        latitude: latitude,
        longitude: longitude,
        radiusMetres: _radiusMetres,
        maxPricePerHour: _maxPrice,
      );
    });
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Find parking'), actions: const [HomeMenuButton()]),
      body: Column(
        children: [
          _Filters(
            lat: _lat,
            lng: _lng,
            radiusMetres: _radiusMetres,
            maxPrice: _maxPrice,
            onRadiusChanged: (value) => setState(() => _radiusMetres = value),
            onMaxPriceChanged: (value) => setState(() => _maxPrice = value),
            onSearch: _search,
          ),
          Expanded(
            child: FutureBuilder<List<NearbySpace>>(
              future: _results,
              builder: (context, snapshot) => AsyncView<List<NearbySpace>>(
                snapshot: snapshot,
                onRetry: _search,
                isEmpty: (data) => data.isEmpty,
                empty: const EmptyState(
                  icon: Icons.location_off_outlined,
                  title: 'Nothing published nearby',
                  message: 'Widen the radius, or raise the price ceiling.',
                ),
                builder: (spaces) => ListView.separated(
                  padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
                  itemCount: spaces.length,
                  separatorBuilder: (_, __) => const SizedBox(height: 12),
                  itemBuilder: (_, i) => _SpaceCard(space: spaces[i]),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _Filters extends StatelessWidget {
  const _Filters({
    required this.lat,
    required this.lng,
    required this.radiusMetres,
    required this.maxPrice,
    required this.onRadiusChanged,
    required this.onMaxPriceChanged,
    required this.onSearch,
  });

  final TextEditingController lat;
  final TextEditingController lng;
  final int radiusMetres;
  final double? maxPrice;
  final ValueChanged<int> onRadiusChanged;
  final ValueChanged<double?> onMaxPriceChanged;
  final VoidCallback onSearch;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
      child: Column(
        children: [
          Row(
            children: [
              Expanded(
                child: TextField(
                  controller: lat,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true, signed: true),
                  decoration: const InputDecoration(labelText: 'Latitude', isDense: true),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: TextField(
                  controller: lng,
                  keyboardType: const TextInputType.numberWithOptions(decimal: true, signed: true),
                  decoration: const InputDecoration(labelText: 'Longitude', isDense: true),
                ),
              ),
            ],
          ),
          const SizedBox(height: 8),
          SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Row(
              children: [
                for (final radius in [1000, 2000, 5000])
                  Padding(
                    padding: const EdgeInsets.only(right: 8),
                    child: ChoiceChip(
                      label: Text(formatDistance(radius.toDouble())),
                      selected: radiusMetres == radius,
                      onSelected: (_) {
                        onRadiusChanged(radius);
                        onSearch();
                      },
                    ),
                  ),
                const SizedBox(width: 8),
                for (final price in [50.0, 100.0])
                  Padding(
                    padding: const EdgeInsets.only(right: 8),
                    child: FilterChip(
                      label: Text('under ${formatCredits(price)}'),
                      selected: maxPrice == price,
                      onSelected: (selected) {
                        onMaxPriceChanged(selected ? price : null);
                        onSearch();
                      },
                    ),
                  ),
                IconButton(onPressed: onSearch, icon: const Icon(Icons.search)),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _SpaceCard extends StatelessWidget {
  const _SpaceCard({required this.space});

  final NearbySpace space;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Card(
      child: InkWell(
        borderRadius: BorderRadius.circular(16),
        onTap: () => context.push(
          '/spaces/${space.id}?title=${Uri.encodeQueryComponent(space.title)}',
        ),
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Row(
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
                  Text(
                    'per hour',
                    style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }
}

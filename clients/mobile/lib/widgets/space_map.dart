import 'package:flutter/material.dart';
import 'package:flutter_map/flutter_map.dart';
import 'package:latlong2/latlong.dart';

import '../core/formatting.dart';
import '../core/models.dart';

/// OpenStreetMap tiles, no API key, no billing account.
///
/// The attribution is not decoration — OSM's licence requires it, and a map that quietly drops it
/// is using the tiles on terms nobody agreed to.
const _osmTiles = 'https://tile.openstreetmap.org/{z}/{x}/{y}.png';

/// A user agent OSM's tile policy asks for, so traffic from this app is identifiable rather than
/// looking like an anonymous scraper and getting blocked.
const _userAgent = 'com.parknest.parknest_mobile';

/// The search results as pins, with the searched-from point marked.
///
/// Tapping a pin selects it rather than navigating: on a map the useful next step is comparing two
/// nearby options, and pushing a screen on first tap makes that a back-and-forth.
class SpaceMap extends StatelessWidget {
  const SpaceMap({
    required this.centre,
    required this.spaces,
    this.selected,
    this.onSelected,
    this.onLongPress,
    super.key,
  });

  final LatLng centre;
  final List<NearbySpace> spaces;
  final NearbySpace? selected;
  final ValueChanged<NearbySpace>? onSelected;

  /// Drops a pin. Used by the listing form to place a space, unused when browsing.
  final ValueChanged<LatLng>? onLongPress;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return FlutterMap(
      options: MapOptions(
        initialCenter: centre,
        initialZoom: 15,
        onLongPress: onLongPress == null ? null : (_, point) => onLongPress!(point),
        interactionOptions: const InteractionOptions(
          // Rotation is easy to trigger by accident and there is no compass to undo it with.
          flags: InteractiveFlag.all & ~InteractiveFlag.rotate,
        ),
      ),
      children: [
        TileLayer(urlTemplate: _osmTiles, userAgentPackageName: _userAgent),
        MarkerLayer(
          markers: [
            Marker(
              point: centre,
              width: 24,
              height: 24,
              child: DecoratedBox(
                decoration: BoxDecoration(
                  color: scheme.primary,
                  shape: BoxShape.circle,
                  border: Border.all(color: scheme.onPrimary, width: 3),
                ),
              ),
            ),
            for (final space in spaces)
              Marker(
                point: LatLng(space.latitude, space.longitude),
                width: 84,
                height: 40,
                child: _PricePin(
                  space: space,
                  isSelected: selected?.id == space.id,
                  onTap: onSelected == null ? null : () => onSelected!(space),
                ),
              ),
          ],
        ),
        const RichAttributionWidget(
          attributions: [TextSourceAttribution('OpenStreetMap contributors')],
        ),
      ],
    );
  }
}

/// The price *is* the pin. On a parking map the question is almost always "which of these is
/// cheapest and close enough", and a generic marker makes you tap each one to find out.
class _PricePin extends StatelessWidget {
  const _PricePin({required this.space, required this.isSelected, this.onTap});

  final NearbySpace space;
  final bool isSelected;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Center(
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          decoration: BoxDecoration(
            color: isSelected ? scheme.primary : scheme.surface,
            borderRadius: BorderRadius.circular(999),
            border: Border.all(color: scheme.primary, width: isSelected ? 2 : 1),
            boxShadow: [
              BoxShadow(
                color: Colors.black.withOpacity(0.18),
                blurRadius: 4,
                offset: const Offset(0, 2),
              ),
            ],
          ),
          child: Text(
            formatCredits(space.pricePerHour),
            style: TextStyle(
              color: isSelected ? scheme.onPrimary : scheme.onSurface,
              fontWeight: FontWeight.w700,
              fontSize: 12,
            ),
          ),
        ),
      ),
    );
  }
}

/// Picks a single point. Used when listing a space, where the pin is the answer rather than a
/// result — so it centres on the current choice and moves it on a tap.
class PointPicker extends StatelessWidget {
  const PointPicker({required this.point, required this.onChanged, super.key});

  final LatLng point;
  final ValueChanged<LatLng> onChanged;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return FlutterMap(
      options: MapOptions(
        initialCenter: point,
        initialZoom: 17,
        onTap: (_, tapped) => onChanged(tapped),
        interactionOptions: const InteractionOptions(
          flags: InteractiveFlag.all & ~InteractiveFlag.rotate,
        ),
      ),
      children: [
        TileLayer(urlTemplate: _osmTiles, userAgentPackageName: _userAgent),
        MarkerLayer(
          markers: [
            Marker(
              point: point,
              width: 40,
              height: 40,
              child: Icon(Icons.location_on, size: 40, color: scheme.primary),
            ),
          ],
        ),
        const RichAttributionWidget(
          attributions: [TextSourceAttribution('OpenStreetMap contributors')],
        ),
      ],
    );
  }
}

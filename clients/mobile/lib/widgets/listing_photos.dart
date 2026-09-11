import 'package:flutter/material.dart';

import '../core/config.dart';
import '../core/models.dart';

/// A listing's photographs, laid out for whoever is looking.
///
/// The URL the API returns is relative — the server decides where photos are served from, and a
/// client that assembled the path itself would break the day storage moves off local disk. This
/// is the one place that joins it to the host, so the join is written once.
String listingPhotoUrl(String url) =>
    url.startsWith('http') ? url : '${AppConfig.apiBaseUrl}$url';

/// The renter's view: a horizontal strip above the booking form.
///
/// Empty means empty. A placeholder graphic pretending to be a photograph of a driveway would be
/// worse than nothing on a screen where the picture is most of what a renter has to judge by.
class ListingPhotoStrip extends StatelessWidget {
  const ListingPhotoStrip({required this.photos, this.height = 180, super.key});

  final List<ListingPhoto> photos;
  final double height;

  @override
  Widget build(BuildContext context) {
    if (photos.isEmpty) {
      return const SizedBox.shrink();
    }

    return SizedBox(
      height: height,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: photos.length,
        separatorBuilder: (_, __) => const SizedBox(width: 8),
        itemBuilder: (context, index) => ClipRRect(
          borderRadius: BorderRadius.circular(12),
          child: PhotoThumbnail(
            url: photos[index].url,
            width: photos.length == 1 ? double.infinity : 260,
            height: height,
          ),
        ),
      ),
    );
  }
}

/// One stored photo, with the two states that actually happen: still loading, and gone.
class PhotoThumbnail extends StatelessWidget {
  const PhotoThumbnail({
    required this.url,
    this.width = 120,
    this.height = 120,
    super.key,
  });

  final String url;
  final double width;
  final double height;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Image.network(
      listingPhotoUrl(url),
      width: width,
      height: height,
      fit: BoxFit.cover,
      loadingBuilder: (context, child, progress) => progress == null
          ? child
          : Container(
              width: width,
              height: height,
              color: scheme.surfaceContainerHighest,
              child: const Center(child: CircularProgressIndicator(strokeWidth: 2)),
            ),
      // A photo the server no longer has is a broken row rather than a broken screen: local disk
      // storage and the database can drift, and the rest of the listing is still worth showing.
      errorBuilder: (context, _, __) => Container(
        width: width,
        height: height,
        color: scheme.surfaceContainerHighest,
        child: Icon(Icons.broken_image_outlined, color: scheme.onSurfaceVariant),
      ),
    );
  }
}

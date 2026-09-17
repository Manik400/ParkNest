import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';

import '../core/api_client.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import '../widgets/listing_photos.dart';

/// A host's own space: its photographs, and the way to its check-in code.
///
/// Photographs are the whole of what a renter has to judge a stranger's driveway by — whether the
/// bay is covered, how tight the turn is, whether a hatchback fits. The API has stored them since
/// Phase 0 and neither client could reach them; this is that gap closed.
class ListingScreen extends StatefulWidget {
  const ListingScreen({required this.spaceId, required this.title, super.key});

  final String spaceId;
  final String title;

  @override
  State<ListingScreen> createState() => _ListingScreenState();
}

class _ListingScreenState extends State<ListingScreen> {
  late Future<List<ListingPhoto>> _photos;
  bool _busy = false;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    setState(() => _photos = Services.of(context).listingPhotos(widget.spaceId));
  }

  Future<void> _add(ImageSource source) async {
    final api = Services.of(context);

    final picked = await ImagePicker().pickImage(
      source: source,
      // Resized before upload. A phone camera produces something several times the server's
      // limit, and a listing photo is looked at on a phone screen.
      maxWidth: 1600,
      imageQuality: 85,
    );

    if (picked == null || !mounted) return;

    setState(() => _busy = true);

    try {
      await api.addListingPhoto(widget.spaceId, picked.path);
      if (mounted) _load();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _remove(ListingPhoto photo) async {
    final api = Services.of(context);

    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Remove this photo?'),
        content: const Text('It disappears from your listing straight away.'),
        actions: [
          TextButton(onPressed: () => Navigator.pop(context, false), child: const Text('Keep')),
          FilledButton(onPressed: () => Navigator.pop(context, true), child: const Text('Remove')),
        ],
      ),
    );

    if (!(confirmed ?? false)) return;

    try {
      await api.removeListingPhoto(widget.spaceId, photo.id);
      if (mounted) _load();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    }
  }

  Future<void> _pickSource() async {
    final source = await showModalBottomSheet<ImageSource>(
      context: context,
      builder: (context) => SafeArea(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            ListTile(
              leading: const Icon(Icons.photo_camera_outlined),
              title: const Text('Take a photo'),
              onTap: () => Navigator.pop(context, ImageSource.camera),
            ),
            ListTile(
              leading: const Icon(Icons.photo_library_outlined),
              title: const Text('Choose from gallery'),
              onTap: () => Navigator.pop(context, ImageSource.gallery),
            ),
          ],
        ),
      ),
    );

    if (source != null) await _add(source);
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(title: Text(widget.title)),
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: [
          SectionCard(
            title: 'Photos',
            child: FutureBuilder<List<ListingPhoto>>(
              future: _photos,
              builder: (context, snapshot) => AsyncView<List<ListingPhoto>>(
                snapshot: snapshot,
                onRetry: _load,
                builder: (photos) => Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    if (photos.isEmpty)
                      Text(
                        'No photos yet. A renter deciding between two driveways is mostly '
                        'deciding on the pictures.',
                        style: TextStyle(color: scheme.onSurfaceVariant),
                      )
                    else
                      Wrap(
                        spacing: 8,
                        runSpacing: 8,
                        children: [
                          for (final photo in photos)
                            Stack(
                              children: [
                                ClipRRect(
                                  borderRadius: BorderRadius.circular(10),
                                  child: PhotoThumbnail(url: photo.url),
                                ),
                                Positioned(
                                  top: -6,
                                  right: -6,
                                  child: IconButton(
                                    onPressed: () => _remove(photo),
                                    icon: const Icon(Icons.cancel),
                                    color: scheme.error,
                                    tooltip: 'Remove',
                                  ),
                                ),
                              ],
                            ),
                        ],
                      ),
                    const SizedBox(height: 16),
                    OutlinedButton.icon(
                      onPressed: _busy ? null : _pickSource,
                      icon: const Icon(Icons.add_a_photo_outlined),
                      label: Text(_busy ? 'Uploading…' : 'Add a photo'),
                    ),
                  ],
                ),
              ),
            ),
          ),
          const SizedBox(height: 16),

          SectionCard(
            title: 'Check-in code',
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text(
                  'The sticker renters scan when they arrive. A scan plus their position is worth '
                  'more than a tap if a session is ever disputed.',
                  style: TextStyle(color: scheme.onSurfaceVariant),
                ),
                const SizedBox(height: 12),
                OutlinedButton.icon(
                  onPressed: () => context.push(
                    '/listings/${widget.spaceId}/check-in-code'
                    '?title=${Uri.encodeComponent(widget.title)}',
                  ),
                  icon: const Icon(Icons.qr_code_2),
                  label: const Text('Show the code'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

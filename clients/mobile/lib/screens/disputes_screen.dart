import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:image_picker/image_picker.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';
import '../widgets/listing_photos.dart';

/// Complaints the user raised, or that were raised against a booking of theirs.
///
/// Deciding a dispute is an operator job in the admin console, and the outcome — including any
/// credits returned — is what this screen reports back. The one thing a party can still do while
/// it is open is add evidence: a photograph of the blocked bay is worth more to a reviewer than
/// any amount of written description, and it is the complainant who is standing there.
class DisputesScreen extends StatefulWidget {
  const DisputesScreen({super.key});

  @override
  State<DisputesScreen> createState() => _DisputesScreenState();
}

class _DisputesScreenState extends State<DisputesScreen> {
  late Future<List<Dispute>> _disputes;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _disputes = Services.of(context).myDisputes();
  }

  Future<void> _reload() async {
    setState(() => _disputes = Services.of(context).myDisputes());
    await _disputes;
  }

  Future<void> _attach(Dispute dispute) async {
    final api = Services.of(context);

    final picked = await ImagePicker().pickImage(
      source: ImageSource.camera,
      maxWidth: 1600,
      imageQuality: 85,
    );

    if (picked == null || !mounted) return;

    try {
      await api.addDisputeEvidence(dispute.disputeId, picked.path);
      if (mounted) {
        showMessage(context, 'Attached. The reviewer will see it.');
        await _reload();
      }
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Scaffold(
      appBar: AppBar(title: const Text('My disputes')),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<List<Dispute>>(
          future: _disputes,
          builder: (context, snapshot) => AsyncView<List<Dispute>>(
            snapshot: snapshot,
            onRetry: _reload,
            isEmpty: (data) => data.isEmpty,
            empty: ListView(
              children: const [
                SizedBox(height: 80),
                EmptyState(
                  icon: Icons.flag_outlined,
                  title: 'No disputes',
                  message: 'Raise one from a booking if something went wrong.',
                ),
              ],
            ),
            builder: (disputes) => ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 8, 16, 24),
              itemCount: disputes.length,
              separatorBuilder: (_, __) => const SizedBox(height: 12),
              itemBuilder: (_, i) {
                final dispute = disputes[i];

                return Card(
                  child: InkWell(
                    borderRadius: BorderRadius.circular(16),
                    onTap: () => context.push('/bookings/${dispute.bookingId}'),
                    child: Padding(
                      padding: const EdgeInsets.all(16),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Row(
                            children: [
                              Expanded(
                                child: Text(
                                  formatDateTime(dispute.createdAt),
                                  style: TextStyle(
                                    color: scheme.onSurfaceVariant,
                                    fontSize: 12,
                                  ),
                                ),
                              ),
                              StatusChip(dispute.status),
                            ],
                          ),
                          const SizedBox(height: 8),
                          Text(dispute.reason),

                          if (dispute.evidence.isNotEmpty) ...[
                            const SizedBox(height: 12),
                            SizedBox(
                              height: 88,
                              child: ListView.separated(
                                scrollDirection: Axis.horizontal,
                                itemCount: dispute.evidence.length,
                                separatorBuilder: (_, __) => const SizedBox(width: 8),
                                itemBuilder: (_, index) => ClipRRect(
                                  borderRadius: BorderRadius.circular(8),
                                  child: PhotoThumbnail(
                                    url: dispute.evidence[index].url,
                                    width: 88,
                                    height: 88,
                                  ),
                                ),
                              ),
                            ),
                          ],

                          // Only while it can still change the outcome. Offering it on a decided
                          // dispute would read as a way to reopen one, and it is not.
                          if (dispute.status == 'Open' || dispute.status == 'UnderReview') ...[
                            const SizedBox(height: 8),
                            Align(
                              alignment: Alignment.centerLeft,
                              child: TextButton.icon(
                                onPressed: () => _attach(dispute),
                                icon: const Icon(Icons.add_a_photo_outlined, size: 18),
                                label: const Text('Add a photo'),
                              ),
                            ),
                          ],

                          if (dispute.resolution case final resolution?) ...[
                            const SizedBox(height: 12),
                            Container(
                              width: double.infinity,
                              padding: const EdgeInsets.all(12),
                              decoration: BoxDecoration(
                                color: scheme.surfaceContainerHighest,
                                borderRadius: BorderRadius.circular(12),
                              ),
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  Text(
                                    'Outcome',
                                    style: TextStyle(
                                      color: scheme.onSurfaceVariant,
                                      fontSize: 12,
                                      fontWeight: FontWeight.w600,
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Text(resolution),

                                  // A dispute can be upheld without money moving, so the refund
                                  // line only appears when there actually was one.
                                  if (dispute.adjustmentAmount case final amount?) ...[
                                    const SizedBox(height: 8),
                                    Text(
                                      '${formatCredits(amount)} returned to your balance',
                                      style: TextStyle(
                                        color: scheme.primary,
                                        fontWeight: FontWeight.w600,
                                      ),
                                    ),
                                  ],
                                ],
                              ),
                            ),
                          ],
                        ],
                      ),
                    ),
                  ),
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}

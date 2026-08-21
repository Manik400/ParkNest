import 'package:flutter/material.dart';

import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// What a user's counterparties have said about them.
///
/// Visible to any signed-in caller, because deciding whether to park in a stranger's driveway —
/// or to hand a stranger the keypad code — is the decision it exists to inform.
class ReputationScreen extends StatefulWidget {
  const ReputationScreen({required this.userId, super.key});

  final String userId;

  @override
  State<ReputationScreen> createState() => _ReputationScreenState();
}

class _ReputationScreenState extends State<ReputationScreen> {
  late Future<Reputation> _reputation;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    setState(() => _reputation = Services.of(context).reputation(widget.userId));
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('Reputation')),
      body: FutureBuilder<Reputation>(
        future: _reputation,
        builder: (context, snapshot) => AsyncView<Reputation>(
          snapshot: snapshot,
          onRetry: _load,
          builder: _build,
        ),
      ),
    );
  }

  Widget _build(Reputation reputation) {
    final scheme = Theme.of(context).colorScheme;

    return ListView(
      padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
      children: [
        SectionCard(
          child: Column(
            children: [
              Text(
                // Not zero for someone nobody has rated. "0.0 stars" against a new host reads as
                // terrible, which is the opposite of what no ratings means.
                reputation.averageScore == null
                    ? 'No ratings yet'
                    : reputation.averageScore!.toStringAsFixed(1),
                style: Theme.of(context).textTheme.headlineMedium,
              ),
              const SizedBox(height: 4),
              StarPicker(score: reputation.averageScore?.round() ?? 0, size: 24),
              const SizedBox(height: 8),
              Text(
                reputation.ratingCount == 1
                    ? 'from one session'
                    : 'from ${reputation.ratingCount} sessions',
                style: TextStyle(color: scheme.onSurfaceVariant),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),

        SectionCard(
          title: 'Trust score',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              LinearProgressIndicator(
                value: reputation.trustScore / 100,
                minHeight: 8,
                borderRadius: BorderRadius.circular(999),
              ),
              const SizedBox(height: 10),
              Text(
                '${reputation.trustScore} / 100. Separate from the stars: it starts from a '
                'presumption of good faith and moves on conduct — leaving a session owing credits '
                'costs far more than one poor review.',
                style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 13),
              ),
            ],
          ),
        ),
        const SizedBox(height: 16),

        if (reputation.recent.isNotEmpty)
          SectionCard(
            title: 'Recent ratings',
            child: Column(
              children: [
                for (final rating in reputation.recent)
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 8),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Row(
                          children: [
                            StarPicker(score: rating.score, size: 16),
                            const Spacer(),
                            Text(
                              formatDate(rating.createdAt),
                              style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
                            ),
                          ],
                        ),
                        if (rating.comment case final comment?) ...[
                          const SizedBox(height: 4),
                          Text(comment),
                        ],
                      ],
                    ),
                  ),
              ],
            ),
          ),
      ],
    );
  }
}

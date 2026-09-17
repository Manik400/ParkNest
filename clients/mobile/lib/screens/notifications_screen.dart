import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/api_client.dart';
import '../core/formatting.dart';
import '../core/models.dart';
import '../widgets/common.dart';

/// Everything the platform has told this user, in the order it happened.
///
/// The durable half of a notification. A socket push reaches a phone that is awake and connected;
/// this list is what a renter finds when they come back up from a basement with the overstay
/// already billing. Opening one marks it read — a badge that outlives reading the thing it points
/// at trains people to ignore the badge.
class NotificationsScreen extends StatefulWidget {
  const NotificationsScreen({super.key});

  @override
  State<NotificationsScreen> createState() => _NotificationsScreenState();
}

class _NotificationsScreenState extends State<NotificationsScreen> {
  late Future<List<AppNotification>> _notifications;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _load();
  }

  void _load() {
    setState(() => _notifications = Services.of(context).myNotifications(limit: 50));
  }

  Future<void> _reload() async {
    _load();
    await _notifications;
  }

  Future<void> _markAllRead() async {
    final api = Services.of(context);

    try {
      await api.markAllNotificationsRead();
      if (mounted) _load();
    } on ApiException catch (error) {
      if (mounted) showError(context, error);
    }
  }

  Future<void> _open(AppNotification notification) async {
    final api = Services.of(context);
    final router = GoRouter.of(context);

    if (!notification.isRead) {
      try {
        await api.markNotificationRead(notification.id);
      } on ApiException {
        // Failing to mark it read is not worth interrupting the tap that was meant to open the
        // booking. The row stays bold and the next tap tries again.
      }
    }

    // Every notification the platform sends is about a booking. When that changes, the kind is
    // the field to switch on — which is why it is carried rather than inferred from the text.
    if (notification.subjectId case final bookingId?) {
      router.push('/bookings/$bookingId');
    } else if (mounted) {
      _load();
    }
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('Notifications'),
        actions: [
          IconButton(
            onPressed: _markAllRead,
            icon: const Icon(Icons.done_all),
            tooltip: 'Mark all read',
          ),
        ],
      ),
      body: RefreshIndicator(
        onRefresh: _reload,
        child: FutureBuilder<List<AppNotification>>(
          future: _notifications,
          builder: (context, snapshot) => AsyncView<List<AppNotification>>(
            snapshot: snapshot,
            onRetry: _reload,
            isEmpty: (data) => data.isEmpty,
            empty: ListView(
              children: const [
                SizedBox(height: 80),
                EmptyState(
                  icon: Icons.notifications_none,
                  title: 'Nothing yet',
                  message: 'Bookings, overstays and credit movements land here.',
                ),
              ],
            ),
            builder: (data) => ListView.separated(
              itemCount: data.length,
              separatorBuilder: (_, __) => const Divider(height: 1),
              itemBuilder: (_, index) => _NotificationTile(
                notification: data[index],
                onTap: () => _open(data[index]),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _NotificationTile extends StatelessWidget {
  const _NotificationTile({required this.notification, required this.onTap});

  final AppNotification notification;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    final unread = !notification.isRead;

    return ListTile(
      onTap: onTap,
      leading: Icon(
        _iconFor(notification.kind),
        color: unread ? scheme.primary : scheme.onSurfaceVariant,
      ),
      title: Text(
        notification.title,
        style: TextStyle(fontWeight: unread ? FontWeight.w700 : FontWeight.w500),
      ),
      subtitle: Text(notification.body),
      trailing: Text(
        formatAgo(notification.createdAt),
        style: TextStyle(color: scheme.onSurfaceVariant, fontSize: 12),
      ),
      isThreeLine: notification.body.length > 60,
    );
  }

  /// Kinds the server does not send yet fall through to the bell rather than to nothing, so a new
  /// notification type ships without an app release looking broken.
  static IconData _iconFor(String kind) => switch (kind) {
        'booking.created' => Icons.event_available_outlined,
        'booking.cancelled' => Icons.event_busy_outlined,
        'session.started' => Icons.local_parking,
        'session.ended' => Icons.exit_to_app,
        'session.violation' || 'overstay.short' => Icons.warning_amber_outlined,
        'overstay.charged' => Icons.timer_off_outlined,
        'payment.captured' || 'payment.failed' || 'wallet.changed' =>
          Icons.account_balance_wallet_outlined,
        'dispute.raised' || 'dispute.resolved' => Icons.flag_outlined,
        _ => Icons.notifications_none,
      };
}

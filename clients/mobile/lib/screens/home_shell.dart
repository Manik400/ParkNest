import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../core/api_client.dart';
import '../widgets/common.dart';

/// The signed-in frame: bottom navigation, and the overflow items that do not deserve a tab.
///
/// Hosting is a tab rather than a separate mode because a ParkNest account is both sides at once —
/// the API authorises per action, not by locking the account into renting or hosting. Hiding it
/// from someone with no listings keeps the bar honest for the common case without pretending the
/// two roles are different accounts.
class HomeShell extends StatelessWidget {
  const HomeShell({required this.state, required this.child, super.key});

  final GoRouterState state;
  final Widget child;

  static const _tabs = [
    (path: '/explore', icon: Icons.search, label: 'Find'),
    (path: '/bookings', icon: Icons.event_available_outlined, label: 'Bookings'),
    (path: '/wallet', icon: Icons.account_balance_wallet_outlined, label: 'Wallet'),
    (path: '/hosting', icon: Icons.home_work_outlined, label: 'Hosting'),
  ];

  @override
  Widget build(BuildContext context) {
    final location = state.matchedLocation;
    final index = _tabs.indexWhere((tab) => location.startsWith(tab.path));

    return Scaffold(
      body: child,
      bottomNavigationBar: NavigationBar(
        selectedIndex: index < 0 ? 1 : index,
        onDestinationSelected: (i) => context.go(_tabs[i].path),
        destinations: [
          for (final tab in _tabs)
            NavigationDestination(icon: Icon(tab.icon), label: tab.label),
        ],
      ),
    );
  }
}

/// The overflow menu, shared by every top-level screen so the same actions sit in the same place.
class HomeMenuButton extends StatelessWidget {
  const HomeMenuButton({super.key});

  @override
  Widget build(BuildContext context) {
    return PopupMenuButton<String>(
      icon: const Icon(Icons.more_vert),
      onSelected: (value) async {
        switch (value) {
          case 'vehicles':
            context.push('/vehicles');
          case 'disputes':
            context.push('/disputes');
          case 'sign-out':
            // Ends the session on the server, not only on the handset. The router notices the
            // cleared session and moves to sign-in by itself.
            await Services.of(context).signOut();
        }
      },
      itemBuilder: (_) => const [
        PopupMenuItem(value: 'vehicles', child: Text('My vehicles')),
        PopupMenuItem(value: 'disputes', child: Text('My disputes')),
        PopupMenuDivider(),
        PopupMenuItem(value: 'sign-out', child: Text('Sign out')),
      ],
    );
  }
}

/// The unread badge, and the way into the notification list.
///
/// Polled, and slowly. The app holds no socket — SignalR pushes reach whatever is connected, and a
/// phone in a basement is not — so this is a count fetched on arrival and refreshed while the
/// screen is open. It is a badge on a bell rather than a figure anyone acts on to the second, and
/// a cheap endpoint asked once a minute is the right amount of machinery for that.
class NotificationsBell extends StatefulWidget {
  const NotificationsBell({super.key});

  @override
  State<NotificationsBell> createState() => _NotificationsBellState();
}

class _NotificationsBellState extends State<NotificationsBell> {
  static const _interval = Duration(minutes: 1);

  int _unread = 0;
  Timer? _timer;

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();

    _timer ??= Timer.periodic(_interval, (_) => _refresh());
    _refresh();
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  Future<void> _refresh() async {
    try {
      final unread = await Services.of(context).unreadNotificationCount();
      if (mounted && unread != _unread) setState(() => _unread = unread);
    } on ApiException {
      // A badge is not worth a snackbar. The count stays as it was and the next tick tries again.
    }
  }

  @override
  Widget build(BuildContext context) {
    return IconButton(
      tooltip: 'Notifications',
      // Refreshed on the way back, because opening the list is what clears them — otherwise the
      // badge sits there stale until the next tick.
      onPressed: () async {
        await context.push('/notifications');
        if (mounted) await _refresh();
      },
      icon: Badge(
        isLabelVisible: _unread > 0,
        // Past a point the exact number stops being information and starts being a wide badge.
        label: Text(_unread > 9 ? '9+' : '$_unread'),
        child: const Icon(Icons.notifications_none),
      ),
    );
  }
}

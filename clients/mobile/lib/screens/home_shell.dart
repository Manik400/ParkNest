import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

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

import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import 'core/api.dart';
import 'core/api_client.dart';
import 'core/session.dart';
import 'screens/booking_detail_screen.dart';
import 'screens/bookings_screen.dart';
import 'screens/disputes_screen.dart';
import 'screens/explore_screen.dart';
import 'screens/home_shell.dart';
import 'screens/hosting_screen.dart';
import 'screens/sign_in_screen.dart';
import 'screens/space_screen.dart';
import 'screens/vehicles_screen.dart';
import 'screens/wallet_screen.dart';
import 'theme.dart';
import 'widgets/common.dart';

class ParkNestApp extends StatefulWidget {
  const ParkNestApp({required this.session, super.key});

  final Session session;

  @override
  State<ParkNestApp> createState() => _ParkNestAppState();
}

class _ParkNestAppState extends State<ParkNestApp> {
  late final ApiClient _client = ApiClient(widget.session);
  late final Api _api = Api(_client, widget.session);
  late final GoRouter _router;

  StreamSubscription<void>? _signedOut;

  @override
  void initState() {
    super.initState();

    _router = GoRouter(
      initialLocation: '/bookings',
      // One redirect decides the signed-in/signed-out split for the whole app. A guard on every
      // route would be the same rule written eight times, and seven of them would be right.
      refreshListenable: widget.session,
      redirect: (context, state) {
        if (!widget.session.isRestored) {
          return null;
        }

        final signedIn = widget.session.isSignedIn;
        final atSignIn = state.matchedLocation == '/sign-in';

        if (!signedIn) {
          return atSignIn ? null : '/sign-in';
        }

        return atSignIn ? '/bookings' : null;
      },
      routes: [
        GoRoute(path: '/sign-in', builder: (_, __) => const SignInScreen()),
        ShellRoute(
          builder: (context, state, child) => HomeShell(state: state, child: child),
          routes: [
            GoRoute(path: '/explore', builder: (_, __) => const ExploreScreen()),
            GoRoute(path: '/bookings', builder: (_, __) => const BookingsScreen()),
            GoRoute(path: '/wallet', builder: (_, __) => const WalletScreen()),
            GoRoute(path: '/hosting', builder: (_, __) => const HostingScreen()),
          ],
        ),
        GoRoute(
          path: '/bookings/:bookingId',
          builder: (_, state) =>
              BookingDetailScreen(bookingId: state.pathParameters['bookingId']!),
        ),
        GoRoute(
          path: '/spaces/:spaceId',
          builder: (_, state) => SpaceScreen(
            spaceId: state.pathParameters['spaceId']!,
            title: state.uri.queryParameters['title'] ?? 'Space',
          ),
        ),
        GoRoute(path: '/vehicles', builder: (_, __) => const VehiclesScreen()),
        GoRoute(path: '/disputes', builder: (_, __) => const DisputesScreen()),
      ],
    );

    // The client discovers a dead session mid-request; the router is what has to act on it.
    _signedOut = _client.onSignedOut.listen((_) => _router.go('/sign-in'));
  }

  @override
  void dispose() {
    _signedOut?.cancel();
    _client.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Services(
      api: _api,
      child: MaterialApp.router(
        title: 'ParkNest',
        debugShowCheckedModeBanner: false,
        theme: buildTheme(Brightness.light),
        darkTheme: buildTheme(Brightness.dark),
        routerConfig: _router,
      ),
    );
  }
}

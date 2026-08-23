import 'dart:async';

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import 'core/api.dart';
import 'core/api_client.dart';
import 'core/push.dart';
import 'core/session.dart';
import 'screens/add_listing_screen.dart';
import 'screens/booking_detail_screen.dart';
import 'screens/bookings_screen.dart';
import 'screens/check_in_code_screen.dart';
import 'screens/disputes_screen.dart';
import 'screens/explore_screen.dart';
import 'screens/home_shell.dart';
import 'screens/hosting_screen.dart';
import 'screens/kyc_screen.dart';
import 'screens/listing_screen.dart';
import 'screens/notifications_screen.dart';
import 'screens/reputation_screen.dart';
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
  late final PushService _push = PushService(_api);
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
        GoRoute(path: '/kyc', builder: (_, __) => const KycScreen()),
        GoRoute(path: '/listings/new', builder: (_, __) => const AddListingScreen()),
        GoRoute(
          path: '/listings/:spaceId',
          builder: (_, state) => ListingScreen(
            spaceId: state.pathParameters['spaceId']!,
            title: state.uri.queryParameters['title'] ?? 'Your space',
          ),
        ),
        GoRoute(
          path: '/listings/:spaceId/check-in-code',
          builder: (_, state) => CheckInCodeScreen(
            spaceId: state.pathParameters['spaceId']!,
            title: state.uri.queryParameters['title'] ?? 'Your space',
          ),
        ),
        GoRoute(path: '/disputes', builder: (_, __) => const DisputesScreen()),
        GoRoute(path: '/notifications', builder: (_, __) => const NotificationsScreen()),
        GoRoute(
          path: '/users/:userId',
          builder: (_, state) => ReputationScreen(userId: state.pathParameters['userId']!),
        ),
      ],
    );

    // The client discovers a dead session mid-request; the router is what has to act on it.
    _signedOut = _client.onSignedOut.listen((_) => _router.go('/sign-in'));

    // Unregistering has to happen while the session is still valid, so it hangs off sign-out
    // rather than off the session listener below — by the time that fires, the tokens are gone.
    _api.beforeSignOut = _push.stop;

    // Registration follows the session: on launch when one was restored, and on the transition
    // when the user signs in. start() is idempotent, so the listener firing for anything else is
    // harmless.
    widget.session.addListener(_syncPush);
    _syncPush();
  }

  void _syncPush() {
    if (widget.session.isSignedIn) {
      unawaited(_push.start());
    }
  }

  @override
  void dispose() {
    widget.session.removeListener(_syncPush);
    _signedOut?.cancel();
    _push.dispose();
    _client.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Services(
      api: _api,
      push: _push,
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

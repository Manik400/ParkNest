import 'package:flutter/material.dart';

import 'app.dart';
import 'core/session.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // Reading the keystore before the first frame, so a signed-in user never sees the sign-in
  // screen flash past on launch.
  final session = Session();
  await session.restore();

  runApp(ParkNestApp(session: session));
}

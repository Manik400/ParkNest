import 'package:flutter/material.dart';

/// One seed colour, two schemes. Material 3 derives the rest, which is the right trade here — a
/// hand-tuned palette would be a lot of decisions to maintain for an app whose screens are mostly
/// lists of money and times.
const _seed = Color(0xFF1B6B4A);

ThemeData buildTheme(Brightness brightness) {
  final scheme = ColorScheme.fromSeed(seedColor: _seed, brightness: brightness);

  return ThemeData(
    useMaterial3: true,
    colorScheme: scheme,
    scaffoldBackgroundColor: scheme.surface,
    appBarTheme: AppBarTheme(
      backgroundColor: scheme.surface,
      surfaceTintColor: Colors.transparent,
      elevation: 0,
      centerTitle: false,
      titleTextStyle: TextStyle(
        color: scheme.onSurface,
        fontSize: 20,
        fontWeight: FontWeight.w600,
      ),
    ),
    cardTheme: CardTheme(
      elevation: 0,
      color: scheme.surfaceContainerLow,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(16)),
      margin: EdgeInsets.zero,
    ),
    inputDecorationTheme: InputDecorationTheme(
      filled: true,
      fillColor: scheme.surfaceContainerHighest.withOpacity(0.4),
      border: OutlineInputBorder(
        borderRadius: BorderRadius.circular(12),
        borderSide: BorderSide.none,
      ),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        minimumSize: const Size.fromHeight(52),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
      ),
    ),
    listTileTheme: const ListTileThemeData(contentPadding: EdgeInsets.symmetric(horizontal: 16)),
  );
}

/// Colour for a status chip.
///
/// `InViolation` is the one that must never read as ordinary — it means a renter left owing
/// credits — so it takes the error colour rather than a neutral grey.
({Color background, Color foreground}) statusColors(ColorScheme scheme, String status) {
  switch (status) {
    case 'Completed':
    case 'Published':
    case 'Paid':
    case 'Resolved':
    case 'Verified':
      return (background: scheme.primaryContainer, foreground: scheme.onPrimaryContainer);

    case 'InViolation':
    case 'Failed':
    case 'Delisted':
      return (background: scheme.errorContainer, foreground: scheme.onErrorContainer);

    case 'Active':
    case 'Held':
    case 'Open':
    case 'UnderReview':
    case 'Requested':
    case 'Draft':
    case 'Paused':
      return (background: scheme.tertiaryContainer, foreground: scheme.onTertiaryContainer);

    default:
      return (
        background: scheme.surfaceContainerHighest,
        foreground: scheme.onSurfaceVariant,
      );
  }
}

/// "InViolation" reads badly in a list; "In violation" does not.
String humaniseStatus(String status) =>
    status.replaceAllMapped(RegExp(r'([a-z])([A-Z])'), (m) => '${m[1]} ${m[2]}');

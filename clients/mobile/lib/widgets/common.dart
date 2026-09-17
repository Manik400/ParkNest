import 'package:flutter/material.dart';

import '../core/api.dart';
import '../core/push.dart';
import '../theme.dart';

/// Hands [Api] down the tree.
///
/// An InheritedWidget rather than a DI package: there is exactly one dependency to provide, and
/// the whole of it is visible on this screen.
class Services extends InheritedWidget {
  const Services({
    required this.api,
    required this.push,
    required super.child,
    super.key,
  });

  final Api api;
  final PushService push;

  static Api of(BuildContext context) {
    final scope = context.dependOnInheritedWidgetOfExactType<Services>();
    assert(scope != null, 'No Services found. Wrap the app in a Services widget.');
    return scope!.api;
  }

  /// Separate from [of] because almost nothing needs it — the bell, so its badge can move the
  /// moment a foreground push lands instead of on the next poll.
  static PushService pushOf(BuildContext context) {
    final scope = context.dependOnInheritedWidgetOfExactType<Services>();
    assert(scope != null, 'No Services found. Wrap the app in a Services widget.');
    return scope!.push;
  }

  @override
  bool updateShouldNotify(Services oldWidget) => api != oldWidget.api || push != oldWidget.push;
}

class StatusChip extends StatelessWidget {
  const StatusChip(this.status, {super.key});

  final String status;

  @override
  Widget build(BuildContext context) {
    final colors = statusColors(Theme.of(context).colorScheme, status);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: colors.background,
        borderRadius: BorderRadius.circular(999),
      ),
      child: Text(
        humaniseStatus(status),
        style: TextStyle(color: colors.foreground, fontSize: 12, fontWeight: FontWeight.w600),
      ),
    );
  }
}

/// The three states every screen in this app has: loading, failed, loaded.
///
/// Worth centralising because the failure case is the one that gets skimped on, and here it is
/// load-bearing — a booking screen that silently shows nothing when the API is down looks
/// identical to one with no bookings.
class AsyncView<T> extends StatelessWidget {
  const AsyncView({
    required this.snapshot,
    required this.builder,
    this.onRetry,
    this.empty,
    this.isEmpty,
    super.key,
  });

  final AsyncSnapshot<T> snapshot;
  final Widget Function(T data) builder;
  final VoidCallback? onRetry;
  final Widget? empty;
  final bool Function(T data)? isEmpty;

  @override
  Widget build(BuildContext context) {
    if (snapshot.connectionState == ConnectionState.waiting && !snapshot.hasData) {
      return const Center(child: Padding(
        padding: EdgeInsets.all(32),
        child: CircularProgressIndicator(),
      ));
    }

    if (snapshot.hasError) {
      return ErrorPanel(message: '${snapshot.error}', onRetry: onRetry);
    }

    final data = snapshot.data;

    if (data == null) {
      return const SizedBox.shrink();
    }

    if (isEmpty?.call(data) ?? false) {
      return empty ?? const SizedBox.shrink();
    }

    return builder(data);
  }
}

class ErrorPanel extends StatelessWidget {
  const ErrorPanel({required this.message, this.onRetry, super.key});

  final String message;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(Icons.error_outline, color: scheme.error, size: 40),
          const SizedBox(height: 12),
          Text(message, textAlign: TextAlign.center),
          if (onRetry != null) ...[
            const SizedBox(height: 16),
            OutlinedButton(onPressed: onRetry, child: const Text('Try again')),
          ],
        ],
      ),
    );
  }
}

class EmptyState extends StatelessWidget {
  const EmptyState({required this.icon, required this.title, this.message, this.action, super.key});

  final IconData icon;
  final String title;
  final String? message;
  final Widget? action;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 48, color: scheme.onSurfaceVariant),
            const SizedBox(height: 16),
            Text(title, style: Theme.of(context).textTheme.titleMedium),
            if (message != null) ...[
              const SizedBox(height: 8),
              Text(
                message!,
                textAlign: TextAlign.center,
                style: TextStyle(color: scheme.onSurfaceVariant),
              ),
            ],
            if (action != null) ...[const SizedBox(height: 20), action!],
          ],
        ),
      ),
    );
  }
}

/// A labelled figure. Used wherever money or time is reported, so the same pair always reads the
/// same way down a column.
class FactRow extends StatelessWidget {
  const FactRow(this.label, this.value, {this.emphasised = false, super.key});

  final String label;
  final String value;
  final bool emphasised;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: Text(label, style: TextStyle(color: scheme.onSurfaceVariant))),
          const SizedBox(width: 12),
          Text(
            value,
            textAlign: TextAlign.right,
            style: TextStyle(
              fontWeight: emphasised ? FontWeight.w700 : FontWeight.w500,
              fontSize: emphasised ? 16 : 14,
            ),
          ),
        ],
      ),
    );
  }
}

class SectionCard extends StatelessWidget {
  const SectionCard({required this.child, this.title, super.key});

  final String? title;
  final Widget child;

  @override
  Widget build(BuildContext context) {
    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (title != null) ...[
              Text(title!, style: Theme.of(context).textTheme.titleMedium),
              const SizedBox(height: 12),
            ],
            child,
          ],
        ),
      ),
    );
  }
}

/// Shows a failure without stealing the screen. Errors here are usually a rule the user tripped
/// over — "Minimum recharge is 100.00 credits" — and a dialog for that is too much ceremony.
void showError(BuildContext context, Object error) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(
      content: Text('$error'),
      backgroundColor: Theme.of(context).colorScheme.errorContainer,
      behavior: SnackBarBehavior.floating,
    ));
}

void showMessage(BuildContext context, String message) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message), behavior: SnackBarBehavior.floating));
}

/// Five taps, one score. Shared by the rating form and anywhere a score is only displayed, so a
/// three-star rating is drawn the same way whether it is being given or read.
class StarPicker extends StatelessWidget {
  const StarPicker({required this.score, this.onChanged, this.size = 36, super.key});

  final int score;

  /// Null makes it a read-only display rather than a disabled control: a rating someone has
  /// already left is not an input the user is temporarily barred from.
  final ValueChanged<int>? onChanged;

  final double size;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;

    return Row(
      mainAxisAlignment: onChanged == null ? MainAxisAlignment.start : MainAxisAlignment.center,
      mainAxisSize: MainAxisSize.min,
      children: [
        for (var star = 1; star <= 5; star++)
          IconButton(
            onPressed: onChanged == null ? null : () => onChanged!(star),
            visualDensity: VisualDensity.compact,
            padding: EdgeInsets.zero,
            constraints: BoxConstraints.tightFor(width: size + 8, height: size + 8),
            icon: Icon(
              star <= score ? Icons.star_rounded : Icons.star_outline_rounded,
              size: size,
              // A filled star keeps its colour when the control is read-only; a greyed-out
              // rating would read as withdrawn rather than recorded.
              color: star <= score ? scheme.primary : scheme.onSurfaceVariant,
            ),
          ),
      ],
    );
  }
}

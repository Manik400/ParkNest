import 'package:flutter/material.dart';

import '../core/models.dart';

/// A city field that opens a searchable list instead of taking free text, so the listing's city
/// is always one the API's catalogue spells the same way — the price band is looked up by name.
class CityPickerField extends StatelessWidget {
  const CityPickerField({
    super.key,
    required this.cities,
    required this.value,
    required this.onChanged,
    this.loading = false,
  });

  final List<CityOption> cities;
  final CityOption? value;
  final ValueChanged<CityOption> onChanged;
  final bool loading;

  @override
  Widget build(BuildContext context) {
    return FormField<CityOption>(
      // Rebuilt with the parent's selection so validation sees what is on screen.
      key: ValueKey(value?.name),
      initialValue: value,
      validator: (city) => city == null ? 'Pick the city the space is in.' : null,
      builder: (field) => InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: loading || cities.isEmpty
            ? null
            : () async {
                final picked = await showCitySearchSheet(context, cities, selected: value);
                if (picked != null) {
                  field.didChange(picked);
                  onChanged(picked);
                }
              },
        child: InputDecorator(
          decoration: InputDecoration(
            labelText: 'City',
            errorText: field.errorText,
            suffixIcon: loading
                ? const Padding(
                    padding: EdgeInsets.all(12),
                    child: SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2)),
                  )
                : const Icon(Icons.arrow_drop_down),
          ),
          isEmpty: value == null,
          child: value == null ? null : Text(value!.label),
        ),
      ),
    );
  }
}

/// A bottom sheet with a search box over the city list. Returns the pick, or null if dismissed.
Future<CityOption?> showCitySearchSheet(
  BuildContext context,
  List<CityOption> cities, {
  CityOption? selected,
}) {
  return showModalBottomSheet<CityOption>(
    context: context,
    isScrollControlled: true,
    showDragHandle: true,
    builder: (context) => _CitySearchSheet(cities: cities, selected: selected),
  );
}

class _CitySearchSheet extends StatefulWidget {
  const _CitySearchSheet({required this.cities, this.selected});

  final List<CityOption> cities;
  final CityOption? selected;

  @override
  State<_CitySearchSheet> createState() => _CitySearchSheetState();
}

class _CitySearchSheetState extends State<_CitySearchSheet> {
  String _query = '';

  @override
  Widget build(BuildContext context) {
    final q = _query.trim().toLowerCase();
    final matches = q.isEmpty
        ? widget.cities
        : widget.cities.where((c) => c.label.toLowerCase().contains(q)).toList();

    return Padding(
      padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
      child: SizedBox(
        height: MediaQuery.sizeOf(context).height * 0.6,
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(16, 0, 16, 8),
              child: TextField(
                autofocus: true,
                decoration: const InputDecoration(
                  hintText: 'Search city',
                  prefixIcon: Icon(Icons.search),
                ),
                onChanged: (text) => setState(() => _query = text),
              ),
            ),
            Expanded(
              child: matches.isEmpty
                  ? const Center(child: Text('No matching city. ParkNest opens new cities on request.'))
                  : ListView.builder(
                      itemCount: matches.length,
                      itemBuilder: (context, i) {
                        final city = matches[i];
                        final isSelected = city.name == widget.selected?.name;
                        return ListTile(
                          title: Text(city.label),
                          trailing: isSelected ? const Icon(Icons.check) : null,
                          selected: isSelected,
                          onTap: () => Navigator.of(context).pop(city),
                        );
                      },
                    ),
            ),
          ],
        ),
      ),
    );
  }
}

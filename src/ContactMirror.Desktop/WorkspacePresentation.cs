using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using ContactMirror.Core;

namespace ContactMirror.Desktop;

public sealed class ContactGroupViewModel : ObservableObject
{
    private readonly Action<ContactGroupViewModel, bool> _select;
    public ContactGroupViewModel(IReadOnlyList<EntryViewModel> members, Action<ContactGroupViewModel, bool> select)
    { Members = members; VisibleMembers = members; _select = select; }
    public IReadOnlyList<EntryViewModel> Members { get; }
    public IReadOnlyList<EntryViewModel> VisibleMembers { get; set; }
    public string Name => Members[0].Entry.Name;
    public string Id => "group-" + Members[0].Entry.Entity + "-" + Members[0].Entry.EntityId;
    public string KindLabel => Members[0].Entry.Entity == EntityKind.Group ? "Ярлык" : "Контакт";
    public string Caption => $"{KindLabel} · изменений: {Members.Count}";
    public string Direction => Members.Any(e => e.IsConflict && !e.CanApply) ? "⚠ Требует решения" : Members.Any(e => !e.Entry.IsSelectable) ? "⚠ Требует исправления" : Members.Any(e => e.Entry.IsDestructiveFor(e.Resolution)) ? "− Удаление" : Members.Any(e => e.ToGoogle) && Members.Any(e => e.ToFolder) ? "↔ В обе стороны" : Members.Any(e => e.ToGoogle) ? "→ В Google" : "← В папку";
    public bool? IsSelected
    {
        get => VisibleMembers.All(e => e.IsSelected) ? true : VisibleMembers.All(e => !e.IsSelected) ? false : null;
        set { if (value is { } selected) _select(this, selected); Refresh(); }
    }
    public bool CanSelect => VisibleMembers.Any(e => e.Entry.IsSelectable && !e.IsRecoveryAlternative && (!e.IsConflict || e.CanApply) && !e.Entry.IsDestructiveFor(e.Resolution));
    public void Refresh() { OnPropertyChanged(nameof(IsSelected)); OnPropertyChanged(nameof(Direction)); OnPropertyChanged(nameof(CanSelect)); }
}

public sealed record DisplayDiffRow(string Section, string Title, string Left, string Right, string Before, string TechnicalPath);
public sealed record ResultRow(string Name, string Field, string Outcome, string Message);

public static class WorkspacePresentation
{
    private static readonly JsonSerializerOptions JsonDisplay = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string FieldTitle(SyncEntry e) => e.Field.StartsWith("$relink:", StringComparison.Ordinal) ? "Связь с записью Google" : e.Field switch { "$retry" => "Повторное создание", "$repairGoogleSnapshot" or "$snapshot" => "Служебная копия Google", "$restoreFile" or "$restoreLocal" => "Восстановление файла", "$restoreDeleteLocal" => "Удаление локального файла", "$restoreUnbind" => "Удаление связи с Google", "$file" => "Файл контакта", "$binding" => "Связь с Google", "$entity" when e.Entity == EntityKind.Group => "Ярлык", _ => CapabilityRegistry.Title(e.Field) };
    public static string SearchText(SyncEntry e) => e.Name + " " + FieldTitle(e) + " " + e.Field + " " + e.Local?.ToJsonString(JsonDisplay) + " " + e.Google?.ToJsonString(JsonDisplay);
    public static IReadOnlyList<DisplayDiffRow> PresentRows(IReadOnlyList<DiffRow> rows, EntryViewModel entry, CancellationToken token)
    {
        if (entry.IsConflict && !entry.IsRecoveryAlternative && !entry.Entry.Field.StartsWith('$'))
            return [new("Изменения", entry.FieldLabel, entry.Entry.Local is null ? "Нет значения" : Format(entry.Entry.Local), entry.Entry.Google is null ? "Нет значения" : Format(entry.Entry.Google), entry.Entry.Before is null ? "Нет значения" : Format(entry.Entry.Before), string.Join("\n", rows.Select(r => r.DisplayPath)))];
        return rows.Where(r => !r.Title.EndsWith(" · тип значения", StringComparison.Ordinal) && r.Section != "Служебная копия Google" && !new[] { ".google", ".metadata", ".etag", ".resourceName", ".clientData" }.Any(p => r.Path.Contains(p, StringComparison.Ordinal)))
            .Select(r => { token.ThrowIfCancellationRequested(); return Present(r, entry); }).ToArray();
    }

    public static DisplayDiffRow Present(DiffRow row, EntryViewModel entry)
    {
        var field = entry.Entry.Field;
        if (field.StartsWith("$restore:", StringComparison.Ordinal)) field = field[9..];
        if (field.StartsWith('$') && row.Path.StartsWith("$.data.", StringComparison.Ordinal)) field = row.Path[7..].Split('.', '[')[0];
        var title = row.Title;
        if (title == "Значение") title = field switch { "biographies" => "Заметка", "phoneNumbers" => "Телефон", "emailAddresses" => "Почта", "urls" => "Ссылка", _ => entry.FieldLabel };
        var local = Readable(row.Local, row.LocalIsJson); var google = Readable(row.Google, row.GoogleIsJson);
        // Restore chooses a backup source and writes it to both sides, unlike ordinary direction choices.
        var restore = entry.Entry.Field.StartsWith("$restore:", StringComparison.Ordinal) || entry.Entry.Field == "$restoreCreate";
        var left = entry.IsConflict ? local : entry.ToGoogle ? google : local;
        var right = entry.IsConflict ? google : restore && entry.Resolution == Resolution.UseGoogle ? google : entry.ToGoogle ? local : google;
        return new(row.Section, title, left, right, Readable(row.Before, row.BeforeIsJson), row.DisplayPath);
    }

    public static string Readable(string value, bool structured = true)
    {
        if (value is "Нет поля" or "Нет значения" or "null" or "[]" || value.StartsWith("\"\"", StringComparison.Ordinal)) return value;
        if (!structured || !value.StartsWith('{') && !value.StartsWith('[')) return value;
        try
        {
            var node = JsonNode.Parse(value);
            return Format(node);
        }
        catch (JsonException) { return value; }
    }
    private static string Format(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonArray array) return array.Count == 0 ? "[]" : string.Join("\n", array.Select(Format));
        if (node is JsonValue scalar) return scalar.TryGetValue<string>(out var text) ? text.Length == 0 ? "\"\" (пустая строка)" : text : scalar.ToJsonString();
        var obj = node.AsObject();
        if (obj.Count == 0) return "{}";
        var labels = new Dictionary<string, string> { ["value"] = "Значение", ["type"] = "Тип", ["contentType"] = "Формат", ["givenName"] = "Имя", ["familyName"] = "Фамилия", ["displayName"] = "Полное имя", ["name"] = "Название", ["formattedValue"] = "Адрес", ["streetAddress"] = "Улица", ["city"] = "Город", ["country"] = "Страна", ["year"] = "Год", ["month"] = "Месяц", ["day"] = "День" };
        return string.Join("\n", obj.Select(p => p.Key == "value" ? Format(p.Value) : $"{labels.GetValueOrDefault(p.Key, p.Key)}: {TypeValue(p.Value)}"));
    }
    private static string TypeValue(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text switch { "home" => "Домашний", "work" => "Рабочий", "mobile" => "Мобильный", _ => Format(value) } : Format(value);

    public static string DeletionDescription(EntryViewModel vm)
    {
        var e = vm.Entry;
        var target = e.DeletesRemote(vm.Resolution) ? "из Google" : "из папки";
        var retained = e.Field == "$restoreUnbind" ? "Локальный файл сохранится; связь с Google будет удалена." : e.Field == "$restoreDeleteLocal" ? "Контакт в Google сохранится." : "Другой стороны уже нет: обычной второй копии не останется.";
        var labels = e.Entity == EntityKind.Group ? " Контакты сохранятся; ярлык будет снят с их связей." : "";
        return $"{e.Name} · {(e.Entity == EntityKind.Group ? "ярлык" : "контакт")} {target}. {retained}{labels}";
    }

    public static (string Summary, IReadOnlyList<ResultRow> Rows) Result(SyncRunResult result, IReadOnlyList<(SyncEntry Entry, Resolution Resolution)> selected)
    {
        var lookup = selected.ToDictionary(x => x.Entry.Key);
        var reported = result.Operations.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var skipped = selected.Where(s => !reported.Contains(s.Entry.Key)).ToArray();
        var failed = result.Operations.Count(o => o.Status == "failed");
        var rows = result.Operations.Select(o =>
        {
            lookup.TryGetValue(o.Key, out var source);
            return new ResultRow(source.Entry?.Name ?? "Результат операции", source.Entry is null ? o.Key : FieldTitle(source.Entry),
                o.Status switch { "confirmed" => "✓ Подтверждено", "unknown" => "? Результат неизвестен", "failed" => "⚠ Не удалось завершить", _ => o.Status },
                o.Message + (o.Status == "failed" ? " Изменения могли уже попасть в Google; выполните новую проверку перед повтором." : ""));
        }).Concat(skipped.Select(s => new ResultRow(s.Entry.Name, FieldTitle(s.Entry), "○ Не выполнялось", "Операция остановлена до этого шага. Выполните новую проверку."))).ToArray();
        var contacts = result.Operations.Where(o => o.Status == "confirmed" && lookup.TryGetValue(o.Key, out var source) && source.Entry.Entity == EntityKind.Contact).Select(o => lookup[o.Key].Entry.EntityId).Distinct().Count();
        var summary = result.IsComplete ? $"Синхронизировано: {result.Confirmed} {Changes(result.Confirmed)}. Подтверждены изменения в {contacts} контактах." : $"Выполнено частично: подтверждено {result.Confirmed}; ошибок завершения: {failed}; не выполнялось: {skipped.Length}; результат неизвестен: {result.Unknown}.";
        if (result.Failed != failed + skipped.Length) summary += $" Координатор сообщает незавершённых операций: {result.Failed}.";
        return (summary, rows);
    }
    public static string Changes(int count) => count % 100 is >= 11 and <= 14 ? "изменений" : (count % 10) switch { 1 => "изменение", 2 or 3 or 4 => "изменения", _ => "изменений" };
}

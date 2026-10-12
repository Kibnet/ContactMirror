using System.Text.Json;
using ContactMirror.Application.Contacts;
using ContactMirror.Infrastructure.Configuration;

namespace ContactMirror.Desktop.Contacts;

public sealed record ContactWorkspacePreferences(ContactTableConfiguration Table, double TableFraction = .54);
public sealed class ContactTablePreferences(bool demo, bool persist, string? filePath = null)
{
    private string FilePath => filePath ?? Path.Combine(ApplicationPaths.DataRoot, demo ? "contact-table-demo.json" : "contact-table.json");
    private readonly Dictionary<string, ContactWorkspacePreferences> workspaces = new();
    public string Notice { get; private set; } = "";
    public ContactWorkspacePreferences Load(Guid workspaceId, string root)
    {
        if (persist && workspaces.Count == 0 && File.Exists(FilePath))
            try
            {
                if (new FileInfo(FilePath).Length > 1024 * 1024) throw new IOException();
                var values = JsonSerializer.Deserialize<Dictionary<string, ContactWorkspacePreferences>>(File.ReadAllText(FilePath));
                if (values is not null) foreach (var pair in values) workspaces[pair.Key] = pair.Value;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { Notice = "Настройки таблицы не удалось прочитать. Восстановлен обычный набор столбцов."; }
        var saved = workspaces.GetValueOrDefault(Key(workspaceId, root));
        saved ??= workspaceId == Guid.Empty ? null : workspaces.GetValueOrDefault(Key(Guid.Empty, root));
        return saved is null ? new(ContactTableConfiguration.Default) : new(ContactTableQuery.Normalize(saved.Table) with { Search = "" }, double.IsFinite(saved.TableFraction) ? Math.Clamp(saved.TableFraction, .25, .8) : .54);
    }
    public void Save(Guid workspaceId, string root, ContactWorkspacePreferences value)
    {
        workspaces[Key(workspaceId, root)] = value with { Table = ContactTableQuery.Normalize(value.Table) with { Search = "" } };
        if (!persist) return;
        try { ApplicationPaths.AtomicWrite(FilePath, JsonSerializer.Serialize(workspaces)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Notice = "Не удалось сохранить настройки таблицы. Контакты не изменены."; }
    }
    private static string Key(Guid id, string root) => id == Guid.Empty ? "unbound:" + ContactMirror.Core.JsonSemantics.Hash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())) : id.ToString("N");
}

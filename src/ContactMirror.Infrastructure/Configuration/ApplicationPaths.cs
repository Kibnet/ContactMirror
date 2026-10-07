using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Configuration;

public static class ApplicationPaths
{
#if CONTACTMIRROR_VALIDATION
    public const string AppId = "ContactMirror.Validation";
    public const string DataDirectoryName = "ContactMirror.Validation.Data";
    public static bool IsSyntheticValidation => true;
    public static bool IsValidation => true;
#elif CONTACTMIRROR_INTEGRATION_VALIDATION
    public const string AppId = "ContactMirror.IntegrationValidation";
    public const string DataDirectoryName = "ContactMirror.IntegrationValidation.Data";
    public static bool IsSyntheticValidation => false;
    public static bool IsValidation => true;
#else
    public const string AppId = "ContactMirror.Desktop";
    public const string DataDirectoryName = "ContactMirror";
    public static bool IsSyntheticValidation => false;
    public static bool IsValidation => false;
#endif
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName);
    public static string ManagedOAuthPath => Path.Combine(DataRoot, "Configuration", "oauth-client.json");
    public static string CredentialPath => Path.Combine(DataRoot, "Credentials", "google.dpapi");
    public static string ApplyingPath => Path.Combine(DataRoot, "update-applying.json");

    public static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(file)) { writer.Write(content); writer.Flush(); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class WorkspacePathPolicy(IEnumerable<string> forbiddenRoots)
{
    private readonly string[] roots = forbiddenRoots.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public static WorkspacePathPolicy Current { get; set; } = new([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationPaths.AppId)]);

    public void Validate(string workspace)
    {
        var path = Canonical(workspace);
        foreach (var root in roots)
            if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new SyncException("workspace-install-directory", "Папка контактов находится в каталоге программы. Выберите отдельную папку: каталог программы заменяется при обновлении.");
    }

    private static string Canonical(string path)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var current = new DirectoryInfo(path);
        var remaining = new Stack<string>();
        while (!current.Exists && current.Parent is not null) { remaining.Push(current.Name); current = current.Parent; }
        var ancestors = new Stack<DirectoryInfo>();
        for (var entry = current; entry is not null; entry = entry.Parent) ancestors.Push(entry);
        string resolved = Path.GetPathRoot(path)!;
        while (ancestors.TryPop(out var entry))
        {
            if (entry.Parent is not null) resolved = Path.Combine(resolved, entry.Name);
            var info = new DirectoryInfo(resolved);
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                resolved = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Не удалось проверить ссылку в пути папки.");
        }
        while (remaining.TryPop(out var name)) resolved = Path.Combine(resolved, name);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }
}

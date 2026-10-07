using System.Text.Json;
using System.Text.Json.Nodes;
using ContactMirror.Application;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace ContactMirror.Infrastructure;

public sealed class FileWorkspaceStore : IWorkspaceStore
{
    private readonly Action<string>? beforeReplace;
    private readonly Action<string>? afterReplace;
    private readonly Action<string>? onDocumentRead;
    public FileWorkspaceStore() { }
    internal FileWorkspaceStore(Action<string>? beforeReplace, Action<string>? afterReplace = null, Action<string>? onDocumentRead = null)
    { this.beforeReplace = beforeReplace; this.afterReplace = afterReplace; this.onDocumentRead = onDocumentRead; }
    public async Task<IWorkspaceSession> OpenAsync(string root, AccountIdentity account, CancellationToken cancellationToken = default)
    {
        WorkspacePathPolicy.Current.Validate(root);
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root)) throw new SyncException("missingFolder", "Папка не найдена. Выберите существующую папку; её отсутствие не считается удалением контактов.");
        ValidatePath(root, root);
        var metadata = Path.Combine(root, ".contactmirror");
        var manifestPath = Path.Combine(metadata, "workspace.json");
        ValidateControlPaths(root);
        var hadManifest = File.Exists(manifestPath);
        if (!hadManifest && Directory.Exists(metadata) && Directory.EnumerateFileSystemEntries(metadata).Any())
            throw new SyncException("missingManifest", "В папке есть состояние ContactMirror, но отсутствует workspace.json. Верните манифест из резервной копии; новая привязка аккаунта не создана.");
        if (!hadManifest && Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) is not ("contacts" or "groups" or "photos" or ".contactmirror")))
            throw new SyncException("foreignFolder", "В этой папке есть другие файлы. Выберите пустую папку или создайте подпапку для контактов.");
        ValidatePath(root, metadata);
        Directory.CreateDirectory(metadata);
        FileStream lease;
        try { ValidateControlPaths(root); lease = new FileStream(Path.Combine(metadata, "workspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new SyncException("workspaceBusy", "Эта папка уже используется другим окном ContactMirror. Дождитесь завершения операции.", inner: ex); }
        try
        {
            if (hadManifest)
            {
                ValidatePath(root, manifestPath);
                var manifest = JsonSemantics.ParseObject(await ReadBoundedBytesAsync(root, manifestPath, 16 * 1024 * 1024, cancellationToken), "workspace.json");
                if (manifest["schemaVersion"]?.GetValue<int>() != 1) throw new SyncException("schemaVersion", "Версия папки не поддерживается этой сборкой.");
                if (manifest["accountKey"]?.GetValue<string>() != account.Key) throw new SyncException("wrongAccount", "Эта папка связана с другим Google-аккаунтом. Выберите другую папку или войдите в прежний аккаунт.");
            }
            else
            {
                var manifest = new JsonObject { ["schemaVersion"] = 1, ["workspaceId"] = Guid.NewGuid().ToString(), ["accountKey"] = account.Key };
                await AtomicNewAsync(root, manifestPath, JsonSemantics.Serialize(manifest), cancellationToken);
            }
            foreach (var name in new[] { "contacts", "groups", "photos", ".contactmirror/backups", ".contactmirror/trash", ".contactmirror/cache", ".contactmirror/recovery/displaced" })
            {
                var folder = Path.Combine(root, name);
                ValidatePath(root, folder);
                Directory.CreateDirectory(folder);
            }
            var dbPath = Path.Combine(metadata, "state.db");
            ValidateControlPaths(root);
            var needsRecovery = hadManifest && !File.Exists(dbPath);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            try
            {
                await connection.OpenAsync(cancellationToken);
                var version = await ScalarAsync(connection, "PRAGMA user_version", cancellationToken);
                if (Convert.ToInt32(version) > 1) throw new SyncException("schemaVersion", "Состояние создано более новой версией ContactMirror.");
                await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS entities(id TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS runs(id TEXT PRIMARY KEY, started TEXT NOT NULL, status TEXT NOT NULL, confirmed INTEGER NOT NULL DEFAULT 0, failed INTEGER NOT NULL DEFAULT 0, unknown INTEGER NOT NULL DEFAULT 0); CREATE TABLE IF NOT EXISTS operations(run_id TEXT NOT NULL, key TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(run_id,key)); CREATE TABLE IF NOT EXISTS flags(name TEXT PRIMARY KEY,value TEXT NOT NULL); PRAGMA user_version=1;", cancellationToken);
                var integrity = Convert.ToString(await ScalarAsync(connection, "PRAGMA quick_check", cancellationToken));
                if (integrity != "ok") throw new SyncException("corruptState", "Состояние папки повреждено. Данные не изменены; восстановите state.db из копии.");
                if (needsRecovery) await ExecuteAsync(connection, "INSERT OR REPLACE INTO flags(name,value) VALUES('recovery','1')", cancellationToken);
                var recovery = Convert.ToString(await ScalarAsync(connection, "SELECT value FROM flags WHERE name='recovery'", cancellationToken)) == "1";
                var session = new Session(root, lease, connection, recovery, beforeReplace, afterReplace, onDocumentRead);
                try { await session.LoadAsync(cancellationToken); }
                catch { await session.DisposeAsync(); throw; }
                return session;
            }
            catch (SqliteException ex)
            {
                await connection.DisposeAsync();
                throw new SyncException("corruptState", "Не удалось прочитать состояние синхронизации. Файлы контактов сохранены; восстановите state.db или откройте копию папки для восстановления.", inner: ex);
            }
            catch { await connection.DisposeAsync(); throw; }
        }
        catch { await lease.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyList<RunSummary>> GetHistoryAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var dbPath = Path.Combine(root, ".contactmirror", "state.db");
        ValidateControlPaths(root);
        if (!File.Exists(dbPath)) return [];
        if (!File.Exists(Path.Combine(root, ".contactmirror", "workspace.json"))) throw new SyncException("missingManifest", "У папки отсутствует манифест workspace.json; чтение состояния остановлено.");
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await db.OpenAsync(cancellationToken);
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,started,status,confirmed,failed,unknown FROM runs ORDER BY started DESC LIMIT 100";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var rows = new List<RunSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.Parse(reader.GetString(0));
            var backupFolder = Path.Combine(root, ".contactmirror", "backups", id.ToString("N"));
            ValidatePath(root, backupFolder);
            long bytes = 0;
            var available = false;
            if (Directory.Exists(backupFolder))
            {
                var inventory = InventoryDirectory(root, backupFolder);
                foreach (var file in inventory.Files) { ValidatePath(root, file); bytes = checked(bytes + new FileInfo(file).Length); }
                var manifest = Path.Combine(backupFolder, "manifest.json"); ValidatePath(root, manifest); available = File.Exists(manifest);
            }
            rows.Add(new(id, DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), bytes, available));
        }
        return rows;
    }

    public async Task<IReadOnlyList<BackupItem>> GetBackupAsync(string root, Guid runId, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var folder = Path.Combine(Path.GetFullPath(root), ".contactmirror", "backups", runId.ToString("N"));
        var manifestPath = Path.Combine(folder, "manifest.json");
        ValidatePath(root, manifestPath);
        if (!File.Exists(manifestPath)) throw new SyncException("missingBackup", "Резервная копия этого запуска не найдена.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) ValidatePath(root, entry);
        var rows = JsonNode.Parse(await ReadBoundedBytesAsync(root, manifestPath, 16 * 1024 * 1024, cancellationToken), documentOptions: new JsonDocumentOptions { MaxDepth = 64 })!.AsArray();
        var result = new List<BackupItem>();
        var identities = new HashSet<(EntityKind, Guid)>();
        var declaredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestPath };
        foreach (var row in rows)
        {
            var id = Guid.Parse(row!["id"]!.GetValue<string>());
            var kind = (EntityKind)row["kind"]!.GetValue<int>();
            if (id == Guid.Empty || !Enum.IsDefined(kind) || !identities.Add((kind, id))) throw InvalidBackup();
            var prefix = Path.Combine(folder, $"{kind}-{id:N}");
            if (row["hashes"] is not JsonObject hashes) throw InvalidBackup();
            string[] suffixes = [".local.json", ".local.photo", ".remote.json", ".remote.photo", ".state.json"];
            if (hashes.Any(p => !suffixes.Contains(p.Key))) throw InvalidBackup();
            async Task<byte[]?> Bytes(string suffix)
            {
                var path = prefix + suffix;
                ValidatePath(root, path);
                var declared = hashes[suffix]?.GetValue<string>();
                if (!File.Exists(path)) { if (declared is not null) throw InvalidBackup(); return null; }
                if (declared is null) throw InvalidBackup();
                declaredPaths.Add(path);
                var bytes = await ReadBoundedBytesAsync(root, path, suffix.EndsWith(".photo", StringComparison.Ordinal) ? 20 * 1024 * 1024 : 16 * 1024 * 1024, cancellationToken);
                if (JsonSemantics.Hash(bytes) != declared) throw InvalidBackup();
                return bytes;
            }
            async Task<JsonObject?> Json(string suffix) { var bytes = await Bytes(suffix); return bytes is null ? null : JsonSemantics.ParseObject(bytes, suffix); }
            var stateDoc = await Json(".state.json");
            var raw = await Bytes(".local.json");
            var localPath = row["localPath"]?.GetValue<string>();
            if (localPath is not null) { localPath = Path.GetFullPath(localPath, root); ValidatePath(root, localPath); }
            result.Add(new(kind, id, raw is null ? null : JsonSemantics.ParseObject(raw, ".local.json"), await Bytes(".local.photo"), await Json(".remote.json"), await Bytes(".remote.photo"), stateDoc?.Deserialize<EntityState>(JsonSemantics.Options), raw, localPath));
        }
        if (Directory.EnumerateFileSystemEntries(folder).Any(path => !declaredPaths.Contains(path))) throw InvalidBackup();
        return result;
    }

    public async Task<string> CreateContactFileAsync(string root, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var contacts = Path.Combine(root, "contacts");
        if (!Directory.Exists(contacts)) throw new SyncException("missingFolder", "Сначала выберите папку и проверьте изменения.");
        var doc = new ContactDocument { Id = Guid.NewGuid(), Data = new JsonObject { ["names"] = new JsonArray(new JsonObject { ["givenName"] = "Новый контакт" }) } };
        var path = Path.Combine(contacts, $"{doc.Id}.contact.json");
        ValidatePath(root, path);
        await AtomicNewAsync(root, path, JsonSemantics.Serialize(doc), cancellationToken);
        return path;
    }

    public static string SafePhotoPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative)) throw new SyncException("unsafePath", "Фото должно иметь относительный путь внутри photos/.");
        var full = Path.GetFullPath(relative.Replace('/', Path.DirectorySeparatorChar), root);
        var photoRoot = Path.GetFullPath(Path.Combine(root, "photos")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(photoRoot, StringComparison.OrdinalIgnoreCase)) throw new SyncException("unsafePath", "Путь фотографии выходит из photos/.");
        ValidatePath(root, full);
        return full;
    }
    public static void ValidatePath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new SyncException("unsafePath", "Путь выходит из выбранной папки.");
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        var components = relative == "." ? Array.Empty<string>() : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var component in new[] { "" }.Concat(components))
        {
            if (component.Length > 0) current = Path.Combine(current, component);
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new SyncException("unsafePath", "Ссылки и перенаправления внутри папки контактов не поддерживаются."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void ValidateControlPaths(string root)
    {
        WorkspacePathPolicy.Current.Validate(root);
        foreach (var name in new[] { ".contactmirror", ".contactmirror/workspace.json", ".contactmirror/workspace.lock", ".contactmirror/state.db", ".contactmirror/state.db-wal", ".contactmirror/state.db-shm", ".contactmirror/state.db-journal" }) ValidatePath(root, Path.Combine(root, name));
    }
    private static SyncException InvalidBackup() => new("invalidBackup", "Резервная копия неполна или изменена. Восстановление остановлено; исходные файлы не заменены.");
    private static (List<string> Files, List<string> Directories) InventoryDirectory(string root, string folder)
    {
        ValidatePath(root, folder);
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<(string Path, int Depth)>(); pending.Push((folder, 0));
        while (pending.TryPop(out var item))
        {
            if (item.Depth > 64 || files.Count + directories.Count > 1_000_000) throw new SyncException("backupTooLarge", "Структура резервной копии превышает допустимые пределы; обработка остановлена.");
            ValidatePath(root, item.Path);
            directories.Add(item.Path);
            foreach (var child in Directory.EnumerateFileSystemEntries(item.Path))
            {
                ValidatePath(root, child);
                if (Directory.Exists(child)) pending.Push((child, item.Depth + 1));
                else files.Add(child);
            }
        }
        return (files, directories);
    }
    private static async Task<byte[]> ReadBoundedBytesAsync(string root, string path, int maxBytes, CancellationToken token)
    {
        ValidatePath(root, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes) throw new SyncException("fileTooLarge", "Файл превышает допустимый размер; чтение и синхронизация остановлены.");
        using var memory = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(memory, token);
        if (memory.Length > maxBytes) throw new SyncException("fileTooLarge", "Файл изменился и превышает допустимый размер.");
        return memory.ToArray();
    }
    private static async Task AtomicNewAsync(string root, string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            ValidatePath(root, path); ValidatePath(root, temp);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, cancellationToken); stream.Flush(true); }
            ValidatePath(root, path); ValidatePath(root, temp);
            File.Move(temp, path, overwrite: false);
        }
        finally { ValidatePath(root, temp); if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<object?> ScalarAsync(SqliteConnection db, string sql, CancellationToken token)
    { ValidateControlPaths(Path.GetDirectoryName(Path.GetDirectoryName(db.DataSource))!); using var command = db.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(token); }
    private static async Task ExecuteAsync(SqliteConnection db, string sql, CancellationToken token)
    { ValidateControlPaths(Path.GetDirectoryName(Path.GetDirectoryName(db.DataSource))!); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(token); }

    private sealed class Session(string root, FileStream lease, SqliteConnection db, bool recovery, Action<string>? beforeReplace, Action<string>? afterReplace, Action<string>? onDocumentRead) : IWorkspaceSession
    {
        public string Root { get; } = root;
        public WorkspaceView View { get; private set; } = null!;
        private readonly Dictionary<Guid, EntityState> _states = [];
        private readonly Dictionary<EntityKind, IdentityIndex> _indexes = [];
        private DirectoryChangeJournal? _photoJournal;
        private readonly Dictionary<string, FileReadCacheLease?> _photoLeases = new(StringComparer.OrdinalIgnoreCase);
        private sealed class IdentityIndex(string folder) : IDisposable
        {
            public string Folder { get; } = folder;
            public DirectoryChangeJournal? Journal { get; set; } = CreateJournal(folder);
            private static DirectoryChangeJournal? CreateJournal(string path)
            {
                if (!OperatingSystem.IsWindows()) return null;
                try { return new(path); } catch (System.ComponentModel.Win32Exception) { return null; }
            }
            public void RefreshAnchor() { Journal?.Dispose(); Journal = CreateJournal(Folder); }
            public Dictionary<string, Guid> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, WorkspaceIssue> Issues { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, FileReadCacheLease?> Leases { get; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, string> PhotoReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
            public void Dispose() { Journal?.Dispose(); foreach (var lease in Leases.Values) lease?.Dispose(); }
        }
        public async Task LoadAsync(CancellationToken token)
        {
            ValidateControlPaths(Root);
            if (OperatingSystem.IsWindows())
            { try { _photoJournal = new(Path.Combine(Root, "photos")); } catch (System.ComponentModel.Win32Exception) { } }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT json FROM entities";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var state = JsonSerializer.Deserialize<EntityState>(reader.GetString(0), JsonSemantics.Options) ?? throw new SyncException("corruptState", "Повреждена запись состояния.");
                    if (!_states.TryAdd(state.Id, state)) throw new SyncException("corruptState", "Повторяющаяся запись состояния.");
                }
            }
            var pending = new List<JournalOperation>();
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT o.json FROM operations o JOIN runs r ON r.id=o.run_id WHERE json_extract(o.json,'$.status') IN ('started','unknown','confirmed')";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token)) pending.Add(JsonSerializer.Deserialize<JournalOperation>(reader.GetString(0), JsonSemantics.Options)!);
            }
            var issues = new List<WorkspaceIssue>();
            await ReadReplacementIssuesAsync(issues, token);
            var contacts = await ScanAsync(EntityKind.Contact, issues, token);
            var groups = await ScanAsync(EntityKind.Group, issues, token);
            View = new() { Contacts = contacts, Groups = groups, States = _states, Issues = issues, Pending = pending, IsRecovery = recovery || issues.Any(i => IsReplacementMarker(i.Path)) || (!ViewExistsState() && contacts.Values.Any(x => x.Document["google"] is not null)) };
        }
        private static bool IsReplacementMarker(string path) => path.EndsWith(".recovery.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".intent.json", StringComparison.OrdinalIgnoreCase);
        private async Task ReadReplacementIssuesAsync(List<WorkspaceIssue> issues, CancellationToken token)
        {
            var displacedFolder = Path.Combine(Root, ".contactmirror", "recovery", "displaced");
            ValidatePath(Root, displacedFolder);
            foreach (var marker in Directory.EnumerateFiles(displacedFolder, "*.recovery.json"))
            {
                ValidatePath(Root, marker);
                var conflict = JsonSemantics.ParseObject(await ReadBoundedBytesAsync(Root, marker, 16 * 1024 * 1024, token), "recovery marker");
                var entityId = Guid.TryParse(conflict["entityId"]?.GetValue<string>(), out var parsedId) ? parsedId : (Guid?)null;
                issues.Add(new(marker, "Другая программа подменила файл во время записи. Правка сохранена в .contactmirror/recovery/displaced; восстановите нужную версию и удалите соответствующий .recovery.json перед повторной синхронизацией.", entityId));
            }
            foreach (var intentPath in Directory.EnumerateFiles(displacedFolder, "*.intent.json"))
            {
                ValidatePath(Root, intentPath);
                Guid? entityId = null;
                try
                {
                    var intent = JsonSemantics.ParseObject(await ReadBoundedBytesAsync(Root, intentPath, 16 * 1024 * 1024, token), "replace intent");
                    if (Guid.TryParse(intent["entityId"]?.GetValue<string>(), out var parsedId)) entityId = parsedId;
                    var filename = intent["displacedFile"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(filename) || Path.GetFileName(filename) != filename || !filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        throw new SyncException("invalidReplacementIntent", "Неверный путь вытесненной версии в журнале записи.");
                    var displacedPath = Path.Combine(displacedFolder, filename);
                    ValidatePath(Root, displacedPath);
                    var diagnostic = "Атомарная запись не получила подтверждение.";
                    if (File.Exists(displacedPath))
                    {
                        var actual = JsonSemantics.Hash(await ReadBoundedBytesAsync(Root, displacedPath, 16 * 1024 * 1024, token));
                        diagnostic += actual == intent["expectedHash"]?.GetValue<string>()
                            ? " Фактически вытесненная версия сохранена и соответствует ожидаемой; завершение операции не подтверждено."
                            : " Фактически вытесненная версия отличается от ожидаемой: конкурентная правка сохранена.";
                    }
                    else diagnostic += " Вытесненный файл ещё не создан или недоступен; результат не подтверждён.";
                    issues.Add(new(intentPath, diagnostic + " Проверьте версии в .contactmirror/recovery/displaced, восстановите нужную и удалите соответствующий .intent.json перед повторной синхронизацией.", entityId));
                }
                catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
                {
                    issues.Add(new(intentPath, "Незавершённый журнал атомарной записи не удалось проверить. Он остаётся заблокированным; исходные и вытесненные файлы не изменены.", entityId));
                }
            }
        }
        private bool ViewExistsState() => _states.Count > 0;
        private async Task<Dictionary<Guid, LocalEntity>> ScanAsync(EntityKind kind, List<WorkspaceIssue> issues, CancellationToken token)
        {
            var folder = Path.Combine(Root, kind == EntityKind.Contact ? "contacts" : "groups");
            if (!_indexes.TryGetValue(kind, out var index)) _indexes.Add(kind, index = new(folder));
            foreach (var lease in index.Leases.Values) lease?.Dispose(); index.Leases.Clear();
            index.Paths.Clear(); index.Issues.Clear(); index.Directories.Clear(); index.PhotoReferences.Clear();
            var result = new Dictionary<Guid, LocalEntity>();
            foreach (var path in SafeEnumerate(folder))
            {
                if (!path.EndsWith(kind == EntityKind.Contact ? ".contact.json" : ".group.json", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    index.Leases[path] = FileReadCacheLease.TryCreate(path);
                    var local = await ReadPathAsync(kind, path, token, index.Leases[path]);
                    if (index.Leases[path] is { } lease && (!lease.MatchesPath(path) || lease.HasChanged()))
                    { lease.Dispose(); index.Leases[path] = null; local = await ReadPathAsync(kind, path, token); }
                    index.Paths[path] = local.Id;
                    if (!result.TryAdd(local.Id, local))
                    {
                        result.Remove(local.Id);
                        issues.Add(new(path, "Два файла содержат одинаковый id. Устраните дубликат до синхронизации.", local.Id));
                    }
                    if (issues.Any(i => i.Id == local.Id)) result.Remove(local.Id);
                }
                catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
                {
                    var issue = new WorkspaceIssue(path, ex is SyncException ? ex.Message : "Не удалось прочитать файл; он не будет заменён или удалён.", Guid.TryParse(Path.GetFileName(path).Split('.')[0], out var id) ? id : null);
                    issues.Add(issue); index.Issues[path] = issue;
                }
            }
            return result;
        }
        private IEnumerable<string> SafeEnumerate(string directory)
        {
            ValidatePath(Root, directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ValidatePath(Root, path);
                if (Directory.Exists(path))
                {
                    foreach (var index in _indexes.Values) if (path.StartsWith(index.Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) index.Directories.Add(path);
                    foreach (var child in SafeEnumerate(path)) yield return child;
                }
                else yield return path;
            }
        }
        private async Task<LocalEntity> ReadPathAsync(EntityKind kind, string path, CancellationToken token, FileReadCacheLease? cachedRead = null)
        {
            ValidatePath(Root, path);
            onDocumentRead?.Invoke(path);
            var bytes = cachedRead is null ? await ReadBoundedBytesAsync(Root, path, 16 * 1024 * 1024, token) : await cachedRead.ReadBytesAsync(16 * 1024 * 1024, token);
            var raw = JsonSemantics.ParseObject(bytes, Path.GetRelativePath(Root, path));
            Guid id;
            byte[]? photo = null;
            if (kind == EntityKind.Contact)
            {
                var contact = DocumentCodec.Contact(raw); id = contact.Id;
                _indexes[kind].PhotoReferences.Remove(path);
                if (contact.Photo is not null)
                {
                    var photoPath = SafePhotoPath(Root, contact.Photo);
                    _indexes[kind].PhotoReferences[path] = photoPath;
                    if (!_photoLeases.TryGetValue(photoPath, out var photoLease) || photoLease is null || photoLease.HasChanged() || !photoLease.MatchesPath(photoPath))
                    { photoLease?.Dispose(); _photoLeases[photoPath] = FileReadCacheLease.TryCreate(photoPath); }
                    if (!File.Exists(photoPath)) throw new SyncException("missingPhoto", "Файл фотографии не найден: " + contact.Photo + ". Для удаления фото установите photo: null.");
                    var cachedPhoto = _photoLeases[photoPath];
                    photo = cachedPhoto is null ? await ReadBoundedBytesAsync(Root, photoPath, 20 * 1024 * 1024, token) : await cachedPhoto.ReadBytesAsync(20 * 1024 * 1024, token);
                    if (cachedPhoto is not null && (!cachedPhoto.MatchesPath(photoPath) || cachedPhoto.HasChanged()))
                    { cachedPhoto.Dispose(); _photoLeases[photoPath] = null; photo = await ReadBoundedBytesAsync(Root, photoPath, 20 * 1024 * 1024, token); }
                    ValidateImage(photo);
                }
            }
            else id = DocumentCodec.Group(raw).Id;
            return new(id, kind, path, raw, JsonSemantics.Hash(bytes), photo, photo is null ? null : JsonSemantics.Hash(photo), bytes);
        }
        public async Task<LocalEntity?> ReadLocalAsync(EntityKind kind, Guid id, CancellationToken cancellationToken = default)
        {
            var issues = new List<WorkspaceIssue>();
            await ReadReplacementIssuesAsync(issues, cancellationToken);
            var index = _indexes[kind];
            if (kind == EntityKind.Contact && _photoJournal is { } photos)
            {
                var photoFolder = Path.Combine(Root, "photos"); ValidatePath(Root, photoFolder);
                if (!photos.MatchesPath(photoFolder))
                { photos.Dispose(); _photoJournal = new(photoFolder); await ScanAsync(kind, [], cancellationToken); }
                else
                {
                    var changes = photos.Poll();
                    if (changes is null) await ScanAsync(kind, [], cancellationToken);
                    else
                    {
                        var changedPhotos = changes.Select(name => Path.GetFullPath(name, photoFolder)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var path in index.PhotoReferences.Where(pair => changedPhotos.Any(changed => pair.Value.Equals(changed, StringComparison.OrdinalIgnoreCase) || pair.Value.StartsWith(changed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).Select(pair => pair.Key).ToArray())
                            await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
                    }
                }
            }
            if (kind == EntityKind.Contact)
            {
                var stalePhotos = _photoLeases.Where(pair => pair.Value is null || pair.Value.HasChanged()).Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var path in index.PhotoReferences.Where(pair => stalePhotos.Contains(pair.Value) || _photoJournal is null).Select(pair => pair.Key).ToArray())
                    await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
            }
            // Names are tracked by directory notifications; content is protected by read-cache oplocks.
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePath(Root, index.Folder);
                if (index.Journal is { } journal && !journal.MatchesPath(index.Folder))
                { index.RefreshAnchor(); await ScanAsync(kind, [], cancellationToken); }
                var changes = index.Journal?.Poll();
                if (changes is { Count: 0 }) break;
                if (attempt == 8) throw new SyncException("localDrift", "Папка непрерывно изменяется другой программой. Повторите проверку после завершения редактирования.");
                var paths = changes?.Select(name => Path.GetFullPath(name, index.Folder)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths is null || paths.Any(path => index.Directories.Contains(path) || Directory.Exists(path)))
                {
                    await ScanAsync(kind, [], cancellationToken);
                    if (index.Journal is null) break; // Safe fallback on platforms without the Windows journal.
                }
                else foreach (var path in paths)
                {
                    ValidatePath(Root, path);
                    if (path.EndsWith(kind == EntityKind.Contact ? ".contact.json" : ".group.json", StringComparison.OrdinalIgnoreCase))
                        await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
                }
            }
            foreach (var path in index.Leases.Where(pair => pair.Value is null || pair.Value.HasChanged()).Select(pair => pair.Key).ToArray())
                await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
            // Always reread selected bytes and photo. Index entries are identities, never a CAS snapshot.
            LocalEntity? current = null;
            foreach (var path in index.Paths.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
            {
                var value = await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
                if (value?.Id == id) current = value;
            }
            // Invalid files with the selected canonical name can be repaired while the session is open.
            foreach (var path in index.Issues.Where(pair => pair.Value.Id == id).Select(pair => pair.Key).ToArray())
            {
                var value = await RefreshIndexedPathAsync(kind, index, path, cancellationToken);
                if (value?.Id == id) current = value;
            }
            issues.AddRange(index.Issues.Values);
            if (issues.Any(x => x.Id is null || x.Id == id)) throw new SyncException("invalidFile", issues.First(x => x.Id is null || x.Id == id).Message);
            if (index.Paths.Count(pair => pair.Value == id) > 1) throw new SyncException("invalidFile", "Два файла содержат одинаковый id. Устраните дубликат до синхронизации.");
            return current;
        }
        private async Task<LocalEntity?> RefreshIndexedPathAsync(EntityKind kind, IdentityIndex index, string path, CancellationToken token)
        {
            index.Paths.Remove(path); index.Issues.Remove(path);
            index.PhotoReferences.Remove(path);
            if (index.Leases.Remove(path, out var previous)) previous?.Dispose();
            ValidatePath(Root, path);
            if (!File.Exists(path)) return null;
            index.Leases[path] = FileReadCacheLease.TryCreate(path);
            try
            {
                var value = await ReadPathAsync(kind, path, token, index.Leases[path]);
                if (index.Leases[path] is { } lease && (!lease.MatchesPath(path) || lease.HasChanged()))
                { lease.Dispose(); index.Leases[path] = null; value = await ReadPathAsync(kind, path, token); }
                index.Paths[path] = value.Id;
                return value;
            }
            catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
            {
                index.Issues[path] = new(path, ex is SyncException ? ex.Message : "Не удалось прочитать файл; он не будет заменён или удалён.", Guid.TryParse(Path.GetFileName(path).Split('.')[0], out var id) ? id : null);
                return null;
            }
        }

        public async Task<Guid> BeginRunAsync(IReadOnlyList<BackupItem> items, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            var id = Guid.NewGuid(); var folder = Path.Combine(Root, ".contactmirror", "backups", id.ToString("N"));
            ValidatePath(Root, folder); Directory.CreateDirectory(folder);
            var manifest = new JsonArray();
            foreach (var item in items)
            {
                if (item.Id == Guid.Empty || !Enum.IsDefined(item.Kind)) throw InvalidBackup();
                var prefix = Path.Combine(folder, $"{item.Kind}-{item.Id:N}");
                var hashes = new JsonObject();
                async Task WriteBytes(string suffix, byte[]? value)
                {
                    if (value is null) return;
                    await AtomicNewAsync(Root, prefix + suffix, value, cancellationToken);
                    hashes[suffix] = JsonSemantics.Hash(value);
                }
                async Task WriteJson(string suffix, JsonObject? value) { if (value is not null) await WriteBytes(suffix, JsonSemantics.Serialize(value)); }
                byte[]? raw = item.RawBytes;
                if (item.LocalDocument is not null && raw is null)
                    throw new SyncException("missingBeforeImage", "Для резервной копии нужны точные исходные байты локального файла. Повторите проверку изменений.");
                if (raw is not null && (item.LocalDocument is null || !JsonSemantics.Equal(JsonSemantics.ParseObject(raw, "before-image"), item.LocalDocument))) throw InvalidBackup();
                string? relativePath = null;
                if (item.LocalPath is not null) { var originalPath = Path.GetFullPath(item.LocalPath, Root); ValidatePath(Root, originalPath); relativePath = Path.GetRelativePath(Root, originalPath); }
                await WriteBytes(".local.json", raw);
                await WriteJson(".remote.json", item.Remote);
                await WriteJson(".state.json", item.State is null ? null : DocumentCodec.ToJson(item.State));
                await WriteBytes(".local.photo", item.LocalPhoto);
                await WriteBytes(".remote.photo", item.RemotePhoto);
                manifest.Add(new JsonObject { ["id"] = item.Id.ToString(), ["kind"] = (int)item.Kind, ["hashes"] = hashes, ["localPath"] = relativePath });
            }
            await AtomicNewAsync(Root, Path.Combine(folder, "manifest.json"), JsonSemantics.Serialize(manifest), cancellationToken);
            ValidateControlPaths(Root);
            using var command = db.CreateCommand(); command.CommandText = "INSERT INTO runs(id,started,status) VALUES($id,$started,'running')";
            command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return id;
        }
        public async Task<string> CacheImageAsync(byte[] bytes, string sourceUrl, CancellationToken cancellationToken = default)
        {
            if (bytes.Length is <= 0 or > 20 * 1024 * 1024) throw new SyncException("cacheImageSize", "Изображение только для чтения должно быть размером до 20 МБ.");
            Google.GoogleContactsGateway.ValidatePhotoUri(sourceUrl);
            var relative = $".contactmirror/cache/{JsonSemantics.Hash(sourceUrl)}-{JsonSemantics.Hash(bytes)}.image";
            var path = Path.GetFullPath(relative, Root);
            ValidatePath(Root, path);
            if (!File.Exists(path))
            {
                try { await AtomicNewAsync(Root, path, bytes, cancellationToken); }
                catch (IOException) when (File.Exists(path)) { /* A parallel cache fill must still match below. */ }
            }
            var cached = await ReadBoundedBytesAsync(Root, path, 20 * 1024 * 1024, cancellationToken);
            if (JsonSemantics.Hash(cached) != JsonSemantics.Hash(bytes)) throw new SyncException("cacheCollision", "Сохранённое изображение только для чтения изменено. Кэш не перезаписан; проверьте файл кэша.");
            return relative;
        }
        public async Task DeleteBackupAsync(Guid runId, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT status,(SELECT COUNT(*) FROM operations WHERE run_id=$id AND json_extract(json,'$.status') IN ('started','unknown','confirmed')) FROM runs WHERE id=$id";
                command.Parameters.AddWithValue("$id", runId.ToString());
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SyncException("missingRun", "Запуск не найден в истории; его резервная копия не удалена.");
                if (reader.GetString(0) is not ("complete" or "recovered") || reader.GetInt64(1) != 0)
                    throw new SyncException("backupInUse", "Резервная копия нужна активному или незавершённому запуску. Сначала разрешите все неподтверждённые операции.");
            }
            var folder = Path.GetFullPath(Path.Combine(Root, ".contactmirror", "backups", runId.ToString("N")));
            ValidatePath(Root, folder);
            if (!Directory.Exists(folder)) return;
            // Validate the whole selected subtree before deleting its first constituent.
            var inventory = InventoryDirectory(Root, folder);
            foreach (var file in inventory.Files) { cancellationToken.ThrowIfCancellationRequested(); ValidatePath(Root, file); File.Delete(file); }
            foreach (var directory in inventory.Directories.OrderByDescending(p => p.Length))
            {
                cancellationToken.ThrowIfCancellationRequested(); ValidatePath(Root, directory); Directory.Delete(directory, recursive: false);
            }
            // History and operation counts remain available after the user removes backup bytes.
        }
        public async Task RecordAsync(Guid runId, JournalOperation operation, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            using var command = db.CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO operations(run_id,key,json) VALUES($run,$key,$json)";
            command.Parameters.AddWithValue("$run", runId.ToString()); command.Parameters.AddWithValue("$key", operation.Key); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(operation, JsonSemantics.Options));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        public async Task SaveStateAsync(EntityState state, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            if (_states.Values.Any(s => s.Id != state.Id && s.Kind == state.Kind && (s.ResourceName == state.ResourceName || state.SourceId is not null && s.SourceId == state.SourceId))) throw new SyncException("identityCollision", "Несколько локальных записей указывают на один контакт Google. Требуется разрешение связи.");
            using var command = db.CreateCommand(); command.CommandText = "INSERT OR REPLACE INTO entities(id,json) VALUES($id,$json)";
            command.Parameters.AddWithValue("$id", state.Id.ToString()); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, JsonSemantics.Options));
            await command.ExecuteNonQueryAsync(cancellationToken); _states[state.Id] = state;
        }
        public async Task RemoveStateAsync(Guid id, CancellationToken cancellationToken = default)
        { ValidateControlPaths(Root); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM entities WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString()); await command.ExecuteNonQueryAsync(cancellationToken); _states.Remove(id); }

        public async Task<LocalEntity> WriteAsync(EntityKind kind, Guid id, JsonObject document, string? expectedHash, byte[]? photoBytes = null, string? expectedPhotoHash = null, bool writePhoto = false, CancellationToken cancellationToken = default)
        {
            var current = await ReadLocalAsync(kind, id, cancellationToken);
            if (current?.Hash != expectedHash || current?.PhotoHash != expectedPhotoHash) throw new SyncException("localDrift", "Файл изменился после сравнения. Новая версия сохранена; проверьте изменения ещё раз.");
            var target = current?.Path ?? Path.Combine(Root, kind == EntityKind.Contact ? "contacts" : "groups", $"{id}.{(kind == EntityKind.Contact ? "contact" : "group")}.json");
            ValidatePath(Root, target);
            var output = (JsonObject)document.DeepClone();
            var photoGuards = new List<FileStream>();
            async Task<byte[]> GuardPhotoAsync(string relative)
            {
                var path = SafePhotoPath(Root, relative);
                var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                photoGuards.Add(guard);
                if (guard.Length > 20 * 1024 * 1024) throw new SyncException("invalidPhoto", "Фото превышает 20 МБ.");
                using var memory = new MemoryStream(); await guard.CopyToAsync(memory, cancellationToken);
                var bytes = memory.ToArray(); ValidateImage(bytes); return bytes;
            }
            try
            {
            if (current?.Document["photo"]?.GetValue<string>() is string oldPhoto)
            {
                var pinned = await GuardPhotoAsync(oldPhoto);
                if (JsonSemantics.Hash(pinned) != expectedPhotoHash) throw new SyncException("localDrift", "Фотография изменилась во время записи. Повторите проверку изменений.");
            }
            if (writePhoto && kind == EntityKind.Contact)
            {
                if (photoBytes is null) output["photo"] = null;
                else
                {
                    ValidateImage(photoBytes);
                    // Content-addressed files keep already-referenced images intact during the JSON commit.
                    var relative = $"photos/{id}-{JsonSemantics.Hash(photoBytes)[..12].ToLowerInvariant()}.{(photoBytes[0] == 0x89 ? "png" : "jpg")}";
                    var photoPath = SafePhotoPath(Root, relative);
                    if (!File.Exists(photoPath)) await AtomicNewAsync(Root, photoPath, photoBytes, cancellationToken);
                    else if (JsonSemantics.Hash(await ReadBoundedBytesAsync(Root, photoPath, 20 * 1024 * 1024, cancellationToken)) != JsonSemantics.Hash(photoBytes)) throw new SyncException("photoCollision", "Файл фото с таким именем содержит другие данные.");
                    output["photo"] = relative;
                }
            }
            var committedId = kind == EntityKind.Contact ? DocumentCodec.Contact(output).Id : DocumentCodec.Group(output).Id;
            if (committedId != id) throw new SyncException("identityChanged", "Идентификатор сохраняемого документа не совпадает с выбранной записью.");
            var committedPhoto = kind == EntityKind.Contact && output["photo"]?.GetValue<string>() is string selectedPhoto ? await GuardPhotoAsync(selectedPhoto) : null;
            var committedBytes = JsonSemantics.Serialize(output);
            await ReplaceAsync(target, committedBytes, expectedHash, cancellationToken);
            _indexes[kind].Paths[target] = id; _indexes[kind].Issues.Remove(target);
            // This snapshot describes the bytes committed by this operation, never a later reader's edit.
            return new(id, kind, target, output, JsonSemantics.Hash(committedBytes), committedPhoto, committedPhoto is null ? null : JsonSemantics.Hash(committedPhoto), committedBytes);
            }
            finally { foreach (var guard in photoGuards) await guard.DisposeAsync(); }
        }
        private async Task ReplaceAsync(string path, byte[] bytes, string? expectedHash, CancellationToken token)
        {
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var displaced = Path.Combine(Root, ".contactmirror", "recovery", "displaced", Guid.NewGuid().ToString("N") + ".json");
            var intentPath = displaced + ".intent.json";
            FileStream? guard = null;
            try
            {
                if (expectedHash is not null)
                {
                    ValidatePath(Root, path);
                    guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (guard.Length > 16 * 1024 * 1024) throw new SyncException("fileTooLarge", "Локальный файл превышает 16 МБ.");
                    using var memory = new MemoryStream(); await guard.CopyToAsync(memory, token);
                    if (JsonSemantics.Hash(memory.ToArray()) != expectedHash) throw new SyncException("localDrift", "Файл изменился во время записи. Новая версия сохранена.");
                }
                else if (File.Exists(path)) throw new SyncException("localDrift", "Файл уже существует. Проверьте изменения заново.");
                ValidatePath(Root, temp);
                await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { await stream.WriteAsync(bytes, token); stream.Flush(true); }
                ValidatePath(Root, path);
                ValidatePath(Root, temp);
                token.ThrowIfCancellationRequested();
                if (expectedHash is null) File.Move(temp, path, overwrite: false);
                else
                {
                    ValidatePath(Root, displaced);
                    var intent = new JsonObject
                    {
                        ["entityId"] = JsonSemantics.ParseObject(bytes, "committed document")["id"]?.DeepClone(),
                        ["target"] = Path.GetRelativePath(Root, path), ["displacedFile"] = Path.GetFileName(displaced),
                        ["expectedHash"] = expectedHash, ["committedHash"] = JsonSemantics.Hash(bytes)
                    };
                    // The durable intent precedes the replace, so a crash cannot hide a displaced edit.
                    await AtomicNewAsync(Root, intentPath, JsonSemantics.Serialize(intent), token);
                    beforeReplace?.Invoke(path);
                    ValidatePath(Root, path); ValidatePath(Root, temp); ValidatePath(Root, displaced);
                    File.Replace(temp, path, displaced, ignoreMetadataErrors: false);
                    afterReplace?.Invoke(path);
                    guard?.Dispose(); guard = null;
                    ValidatePath(Root, displaced);
                    // A competing atomic rename can replace the pathname despite the read guard.
                    // File.Replace retains the exact file actually displaced; never delete that image.
                    byte[] before;
                    await using (var durable = new FileStream(displaced, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
                    {
                        if (durable.Length > 16 * 1024 * 1024) throw new SyncException("localDrift", "Фактически вытесненный файл превышает 16 МБ; он сохранён в .contactmirror/recovery/displaced.");
                        durable.Flush(true);
                        using var memory = new MemoryStream(); await durable.CopyToAsync(memory, CancellationToken.None); before = memory.ToArray();
                    }
                    if (JsonSemantics.Hash(before) != expectedHash)
                    {
                        var conflict = (JsonObject)intent.DeepClone();
                        conflict["actualDisplacedHash"] = JsonSemantics.Hash(before);
                        await AtomicNewAsync(Root, displaced + ".recovery.json", JsonSemantics.Serialize(conflict), CancellationToken.None);
                        ValidatePath(Root, intentPath); File.Delete(intentPath);
                        throw new SyncException("localDrift", "Файл подменён другой программой во время атомарной записи. Обе версии сохранены: фактически вытесненная — в .contactmirror/recovery/displaced. Повторите проверку.");
                    }
                    ValidatePath(Root, intentPath); File.Delete(intentPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new SyncException("fileBusy", "Файл занят или запись запрещена. Исходные и вытесненные версии сохранены; повторите проверку.", inner: ex); }
            finally { guard?.Dispose(); ValidatePath(Root, temp); if (File.Exists(temp)) File.Delete(temp); }
        }
        public async Task TrashAsync(EntityKind kind, Guid id, string expectedHash, Guid runId, CancellationToken cancellationToken = default)
        {
            var local = await ReadLocalAsync(kind, id, cancellationToken);
            if (local is null || local.Hash != expectedHash) throw new SyncException("localDrift", "Файл изменился перед перемещением в корзину.");
            var folder = Path.Combine(Root, ".contactmirror", "trash", runId.ToString("N")); ValidatePath(Root, folder); Directory.CreateDirectory(folder);
            ValidatePath(Root, local.Path);
            using var guard = new FileStream(local.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var memory = new MemoryStream(); await guard.CopyToAsync(memory, cancellationToken);
            if (JsonSemantics.Hash(memory.ToArray()) != expectedHash) throw new SyncException("localDrift", "Файл изменился перед удалением; данные сохранены.");
            var destination = Path.Combine(folder, Path.GetFileName(local.Path)); ValidatePath(Root, destination); ValidatePath(Root, local.Path);
            File.Move(local.Path, destination, overwrite: false);
            _indexes[kind].Paths.Remove(local.Path); _indexes[kind].Issues.Remove(local.Path);
            if (_indexes[kind].Leases.Remove(local.Path, out var cached)) cached?.Dispose();
            ValidatePath(Root, destination);
            if (JsonSemantics.Hash(await ReadBoundedBytesAsync(Root, destination, 16 * 1024 * 1024, CancellationToken.None)) != expectedHash)
                throw new SyncException("localDrift", "Во время перемещения файл был подменён. Фактически перемещённая версия сохранена в корзине; повторите проверку.");
        }
        public async Task CompleteRunAsync(Guid runId, SyncRunResult result, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            using var command = db.CreateCommand(); command.CommandText = "UPDATE runs SET status=$status,confirmed=$c,failed=$f,unknown=$u WHERE id=$id";
            command.Parameters.AddWithValue("$status", result.IsComplete ? "complete" : "partial"); command.Parameters.AddWithValue("$c", result.Confirmed); command.Parameters.AddWithValue("$f", result.Failed); command.Parameters.AddWithValue("$u", result.Unknown); command.Parameters.AddWithValue("$id", runId.ToString()); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        public Task AcknowledgeRecoveryAsync(CancellationToken cancellationToken = default) => ExecuteAsync(db, "DELETE FROM flags WHERE name='recovery'; UPDATE runs SET status='recovered' WHERE status IN ('running','partial') AND NOT EXISTS (SELECT 1 FROM operations WHERE run_id=runs.id AND json_extract(json,'$.status') IN ('started','unknown','confirmed'));", cancellationToken);
        public async Task ResolvePendingAsync(Guid entityId, string? field = null, CancellationToken cancellationToken = default)
        {
            ValidateControlPaths(Root);
            using var command = db.CreateCommand(); command.CommandText = "UPDATE operations SET json=json_set(json,'$.status','reconciled') WHERE json_extract(json,'$.entityId')=$id AND ($field IS NULL OR json_extract(json,'$.field')=$field) AND json_extract(json,'$.status') IN ('started','unknown','confirmed')";
            command.Parameters.AddWithValue("$id", entityId.ToString()); command.Parameters.AddWithValue("$field", (object?)field ?? DBNull.Value); await command.ExecuteNonQueryAsync(cancellationToken);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var index in _indexes.Values) index.Dispose();
            _photoJournal?.Dispose();
            foreach (var photo in _photoLeases.Values) photo?.Dispose();
            await db.DisposeAsync(); await lease.DisposeAsync();
        }
    }

    public static void ValidateImage(byte[] bytes)
    {
        if (bytes.Length is < 16 or > 20 * 1024 * 1024) throw new SyncException("invalidPhoto", "Фото должно быть JPEG/PNG размером до 20 МБ.");
        var png = bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 13, 10, 26, 10 });
        var jpeg = bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
        if (!png && !jpeg) throw new SyncException("invalidPhoto", "Файл фотографии должен быть JPEG или PNG.");
        if (png && bytes.Length < 33) throw new SyncException("invalidPhoto", "PNG-файл обрезан или повреждён.");
        if (png)
        {
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
            if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 40_000_000) throw new SyncException("invalidPhoto", "Фотография слишком большая (до 8192×8192, не более 40 Мп).");
            var offset = 8;
            var hasData = false;
            var complete = false;
            while (offset <= bytes.Length - 12)
            {
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                if ((long)offset + length + 12 > bytes.Length) break;
                var chunk = bytes.AsSpan(offset + 4, 4);
                hasData |= chunk.SequenceEqual("IDAT"u8);
                if (chunk.SequenceEqual("IEND"u8)) { complete = length == 0 && hasData; break; }
                offset += checked((int)length + 12);
            }
            if (!complete) throw new SyncException("invalidPhoto", "PNG-файл обрезан или не содержит завершённого изображения.");
        }
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(bytes));
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg)) throw new SyncException("invalidPhoto", "Фотография должна быть полноценным PNG или JPEG.");
            var info = codec.Info;
            if (info.Width is <= 0 or > 8192 || info.Height is <= 0 or > 8192 || (long)info.Width * info.Height > 40_000_000 || codec.FrameCount > 1)
                throw new SyncException("invalidPhoto", "Фотография превышает 8192×8192 или 40 Мп, либо содержит анимацию.");
            var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var pixels = new SKBitmap(decodeInfo);
            if (codec.GetPixels(decodeInfo, pixels.GetPixels()) != SKCodecResult.Success)
                throw new SyncException("invalidPhoto", "Не удалось полностью декодировать фотографию: файл обрезан или повреждён.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { throw new SyncException("invalidPhoto", "Фотография повреждена или имеет неподдерживаемый формат.", inner: ex); }
    }
}

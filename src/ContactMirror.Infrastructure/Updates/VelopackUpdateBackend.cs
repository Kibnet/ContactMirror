using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ContactMirror.Application.Updates;
using ContactMirror.Infrastructure.Configuration;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace ContactMirror.Infrastructure.Updates;

public sealed class VelopackUpdateBackend : IApplicationUpdateBackend
{
    private readonly UpdateManager? manager;
    private readonly IVelopackLocator locator;
    private readonly UpdateSourceOptions source;
    private readonly UpdateHandoff handoff = new(ApplicationPaths.ApplyingPath);
    private readonly string owner;
    private readonly bool demo;
    private UpdateInfo? prepared;
    private string? targetAssemblyHash;
    public bool HasAmbiguousApply => handoff.IsPending;
    public bool IsSupported => locator.CurrentlyInstalledVersion is not null && locator.AppId == ApplicationPaths.AppId;
    public bool IsConfigured => source.IsConfigured;
    public string CurrentVersion => locator.CurrentlyInstalledVersion?.ToString() ?? Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.2.0";
    public string SourceIdentity => GetSourceIdentity(source);
    public static string GetSourceIdentity(UpdateSourceOptions source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Kind + "\n" + source.Url + "\n" + source.Channel)));

    public VelopackUpdateBackend(UpdateSourceOptions source, string owner, bool demo = false, IVelopackLocator? locator = null)
    {
        this.source = source; this.owner = owner; this.demo = demo; this.locator = locator ?? VelopackLocator.Current;
        if (IsSupported && IsConfigured)
        {
            IUpdateSource updateSource = source.Kind switch
            {
                "github" => new GithubSource(source.Url, null, false),
                "file" => new SimpleFileSource(new DirectoryInfo(source.Url)),
                _ => new SimpleWebSource(source.Url)
            };
            manager = new(updateSource, new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false }, this.locator);
        }
    }
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        if (manager is null) throw new InvalidOperationException("Источник обновлений не настроен.");
        // SDK1.2 check has no cancellation parameter; keep its task owned until it completes.
        cancellationToken.ThrowIfCancellationRequested();
        var update = await manager.CheckForUpdatesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (update is not null) ValidateIdentity(update.TargetFullRelease);
        prepared = update; targetAssemblyHash = null;
        return update?.TargetFullRelease.Version.ToString();
    }
    public async Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken)
    {
        if (manager is null || prepared is null) throw new InvalidOperationException("Сначала проверьте обновления.");
        ValidateIdentity(prepared.TargetFullRelease);
        await manager.DownloadUpdatesAsync(prepared, progress.Report, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try { targetAssemblyHash = VerifyPackage(prepared.TargetFullRelease); }
        catch (InvalidDataException)
        {
            // SDK1.2 skips checksum validation for an existing cached full package.
            // A delta reconstruction can also produce a different ZIP container hash.
            // Fetch the original full asset once; only the exact feed hash is accepted.
            File.Delete(PackagePath(prepared.TargetFullRelease));
            await manager.DownloadUpdatesAsync(new UpdateInfo(prepared.TargetFullRelease, false), progress.Report, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            try { targetAssemblyHash = VerifyPackage(prepared.TargetFullRelease); }
            catch (InvalidDataException) { File.Delete(PackagePath(prepared.TargetFullRelease)); throw; }
        }
    }
    private void ValidateIdentity(VelopackAsset asset)
    {
        if (asset.PackageId != ApplicationPaths.AppId || asset.Type != VelopackAssetType.Full || asset.Version <= locator.CurrentlyInstalledVersion || Path.GetFileName(asset.FileName) != asset.FileName || !asset.FileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Пакет предназначен для другого приложения или версии.");
    }
    private string PackagePath(VelopackAsset asset)
    {
        ValidateIdentity(asset);
        return Path.Combine(locator.PackagesDir ?? throw new IOException("Каталог пакетов отсутствует."), asset.FileName);
    }
    private string VerifyPackage(VelopackAsset asset) => VerifyPackageFile(PackagePath(asset), asset, ApplicationPaths.AppId);
    public static string VerifyPackageFile(string path, VelopackAsset asset, string expectedAppId)
    {
        using (var file = File.OpenRead(path))
        {
            if (file.Length != asset.Size || string.IsNullOrWhiteSpace(asset.SHA256) || !Convert.ToHexString(SHA256.HashData(file)).Equals(asset.SHA256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Пакет обновления повреждён.");
        }
        using var zip = ZipFile.OpenRead(path);
        var specs = zip.Entries.Where(x => x.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
        if (specs.Length != 1 || specs[0].Length > 64 * 1024) throw new InvalidDataException("Метаданные пакета отсутствуют.");
        using (var metadataStream = specs[0].Open())
        using (var reader = XmlReader.Create(metadataStream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 }))
        {
            var xml = XDocument.Load(reader);
            var metadata = xml.Root?.Elements().SingleOrDefault(x => x.Name.LocalName == "metadata");
            string? Value(string name) => metadata?.Elements().SingleOrDefault(x => x.Name.LocalName == name)?.Value;
            if (asset.PackageId != expectedAppId || Value("id") != expectedAppId || Value("version") != asset.Version.ToString() || Value("channel") != "win" || Value("rid") != "win-x64" || Value("os") != "win" || Value("machineArchitecture") != "x64" || Value("mainExe") != "ContactMirror.exe")
                throw new InvalidDataException("Пакет не предназначен для ContactMirror Windows x64.");
        }
        var entries = zip.Entries.Where(x => x.FullName == "lib/app/ContactMirror.dll").ToArray();
        if (entries.Length != 1 || entries[0].Length > 32 * 1024 * 1024) throw new InvalidDataException("В пакете отсутствует приложение ContactMirror.");
        using var assembly = entries[0].Open();
        return Convert.ToHexString(SHA256.HashData(assembly));
    }
    public void ApplyAndRestart()
    {
        if (manager is null || prepared is null || targetAssemblyHash is null) throw new InvalidOperationException("Обновление ещё не скачано.");
        ValidateIdentity(prepared.TargetFullRelease);
        var verifiedHash = VerifyPackage(prepared.TargetFullRelease);
        if (verifiedHash != targetAssemblyHash) throw new InvalidDataException("Пакет изменился после загрузки.");
        var marker = handoff.Prepare(ApplicationPaths.AppId, owner, SourceIdentity, prepared.TargetFullRelease.Version.ToString(), verifiedHash,
            CurrentVersion, UpdateHandoff.HashAssembly(Path.Combine(AppContext.BaseDirectory, "ContactMirror.dll")));
        var arguments = new List<string> { "--resume-update", marker.Nonce };
        if (demo) arguments.Add("--demo");
        // This SDK call launches Update.exe and exits immediately. The marker fences second launches.
        manager.ApplyUpdatesAndRestart(prepared.TargetFullRelease, arguments.ToArray());
    }
}

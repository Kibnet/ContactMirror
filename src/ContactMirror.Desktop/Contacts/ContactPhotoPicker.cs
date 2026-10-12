using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ContactMirror.Core;
using ContactMirror.Infrastructure;

namespace ContactMirror.Desktop.Contacts;

public static class ContactPhotoPicker
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    public static Task<bool> PickAsync(TopLevel owner, ContactsViewModel vm)
    {
        if (!owner.StorageProvider.CanOpen) return Task.FromResult(false);
        return PickAsync(vm, async () =>
        {
            var selected = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Фотография контакта", AllowMultiple = false,
                FileTypeFilter = [new("Изображения PNG или JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"], MimeTypes = ["image/png", "image/jpeg"] }]
            });
            return selected.Count == 0 ? null : await selected[0].OpenReadAsync();
        });
    }

    /// <summary>The injected stream opener follows the same modal, bounded read and snapshot guards as the native picker.</summary>
    public static async Task<bool> PickAsync(ContactsViewModel vm, Func<Task<Stream?>> openSelectedStream)
    {
        if (!vm.IsEditing || !vm.CanEdit || vm.IsPickingPhoto) return false;
        var document = vm.Document; var id = vm.Original?.Id; var generation = vm.Guard.Generation;
        byte[]? bytes = null; string? failure = null;
        vm.IsPickingPhoto = true;
        try
        {
            await using var input = await openSelectedStream();
            if (input is not null)
                bytes = await Task.Run(async () =>
                {
                    using var output = new MemoryStream(); var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                        if (count == 0) break;
                        if (output.Length + count > MaximumBytes)
                            throw new SyncException("photoTooLarge", "Фотография больше 20 МБ. Выберите меньший файл.");
                        await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                    }
                    var selectedBytes = output.ToArray(); FileWorkspaceStore.ValidateImage(selectedBytes); return selectedBytes;
                });
        }
        catch (OperationCanceledException) { }
        catch (SyncException error) { failure = error.Message; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        { failure = "Не удалось прочитать фотографию: " + error.Message; }
        finally { vm.IsPickingPhoto = false; }
        if (!ReferenceEquals(document, vm.Document) || id != vm.Original?.Id || generation != vm.Guard.Generation || !vm.IsEditing || !vm.CanEdit)
        { vm.Status = "Выбор фотографии отменён: карточка или доступ к ней изменились."; return false; }
        if (failure is not null)
        {
            vm.ValidationError("", "photo"); vm.Error = failure;
            vm.Status = "Фотография не изменена. Выберите другой файл."; return false;
        }
        if (bytes is null) return false;
        vm.ValidationError("", "photo");
        var changed = vm.ChangeValidatedPhoto(bytes);
        if (changed) vm.Status = "Фотография выбрана. Сохраните контакт в папку.";
        else if (vm.PhotoBytes is { } current && current.AsSpan().SequenceEqual(bytes)) vm.Status = "Выбрана та же фотография.";
        return changed;
    }
}

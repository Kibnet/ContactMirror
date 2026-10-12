using AppAutomation.Avalonia.Headless.Session;
using ContactMirror.Desktop.Contacts;
using SkiaSharp;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    [Test, NotInParallel("DesktopUi")]
    public async Task Delayed_photo_picker_blocks_mutation_navigation_save_and_cancel_writes_nothing()
    {
        using var card = await OpenCard(CardDocument());
        var selected = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> picking = null!; var generation = card.Model.Guard.Generation; var id = card.Model.Original!.Id;
        HeadlessRuntime.Dispatch(() => { picking = ContactPhotoPicker.PickAsync(card.Model, () => selected.Task); });
        await Assert.That(card.Model.IsPickingPhoto).IsTrue(); await Assert.That(card.Model.HasModal).IsTrue();
        await Assert.That(card.Model.CanEdit).IsFalse(); await Assert.That(card.Model.CanCreate).IsFalse();
        var changed = true;
        HeadlessRuntime.Dispatch(() => { changed = card.Model.Mutate(doc => doc["starred"] = true); card.Model.CancelEdit(); });
        await Assert.That(changed).IsFalse(); await Assert.That(card.Model.Original!.Id).IsEqualTo(id);
        await Assert.That(card.Model.IsEditing).IsTrue();
        await Assert.That(await OnCardUi(() => card.Model.SaveAsync())).IsFalse();
        await Assert.That(await OnCardUi(() => card.Model.ResolveDraftAsync())).IsFalse();
        Task select = null!; HeadlessRuntime.Dispatch(() => { select = card.Model.SelectAsync(Guid.NewGuid()); }); await select;
        await Assert.That(card.Model.Original!.Id).IsEqualTo(id);
        var reopenCalls = 0;
        await Assert.That(await OnCardUi(() => ContactPhotoPicker.PickAsync(card.Model, () => { reopenCalls++; return Task.FromResult<Stream?>(null); }))).IsFalse();
        await Assert.That(reopenCalls).IsEqualTo(0);
        selected.SetResult(null);
        await Assert.That(await OnCardUi(()=>picking)).IsFalse(); await Assert.That(card.Model.IsPickingPhoto).IsFalse();
        await Assert.That(card.Model.IsDirty).IsFalse(); await Assert.That(card.Model.PhotoBytes).IsNull();
        await Assert.That(card.Model.Guard.Generation).IsEqualTo(generation); await Assert.That(card.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Photo_stream_reads_on_worker_and_copies_valid_image_into_the_captured_draft()
    {
        using var card = await OpenCard(CardDocument());
        var bytes = PickerPng(); var expected = bytes.ToArray(); var stream = new DelayedImageStream(bytes);
        Task<bool> picking = null!; var uiThread = 0;
        HeadlessRuntime.Dispatch(() => { uiThread = Environment.CurrentManagedThreadId; picking = ContactPhotoPicker.PickAsync(card.Model, () => Task.FromResult<Stream?>(stream)); });
        var readerThread = await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(readerThread).IsNotEqualTo(uiThread); await Assert.That(card.Model.IsPickingPhoto).IsTrue();
        var timer = System.Diagnostics.Stopwatch.StartNew(); HeadlessRuntime.Dispatch(() => { _ = card.Model.Name; }); timer.Stop();
        await Assert.That(timer.ElapsedMilliseconds).IsLessThan(250);
        stream.Release.SetResult();
        await Assert.That(await OnCardUi(()=>picking)).IsTrue(); await Assert.That(card.Model.IsPickingPhoto).IsFalse();
        await Assert.That(card.Model.IsDirty).IsTrue(); await Assert.That(card.Model.PhotoBytes!.SequenceEqual(expected)).IsTrue();
        bytes[0] = 0;
        await Assert.That(card.Model.PhotoBytes!.SequenceEqual(expected)).IsTrue(); await Assert.That(card.Store.Saves).IsEqualTo(0);
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Picker_discards_selected_image_if_draft_generation_changed_while_dialog_was_open()
    {
        using var card = await OpenCard(CardDocument());
        var selected = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> picking = null!;
        HeadlessRuntime.Dispatch(() => { picking = ContactPhotoPicker.PickAsync(card.Model, () => selected.Task); });
        HeadlessRuntime.Dispatch(() => card.Model.Guard.Reset());
        selected.SetResult(new MemoryStream(PickerPng(), writable: false));
        await Assert.That(await OnCardUi(()=>picking)).IsFalse(); await Assert.That(card.Model.PhotoBytes).IsNull();
        await Assert.That(card.Model.IsDirty).IsFalse(); await Assert.That(card.Model.Status).Contains("карточка");
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Picker_rejects_actual_oversize_stream_and_bad_image_without_changing_photo_or_dirty_state()
    {
        using var card = await OpenCard(CardDocument());
        await Assert.That(await OnCardUi(() => ContactPhotoPicker.PickAsync(card.Model,
            () => Task.FromResult<Stream?>(new MemoryStream(new byte[ContactPhotoPicker.MaximumBytes + 1], writable: false))))).IsFalse();
        await Assert.That(card.Model.Error).Contains("20 МБ"); await Assert.That(card.Model.IsPickingPhoto).IsFalse();
        await Assert.That(card.Model.PhotoBytes).IsNull(); await Assert.That(card.Model.IsDirty).IsFalse();
        HeadlessRuntime.Dispatch(() => card.Model.ValidationError("", "photo"));
        await Assert.That(await OnCardUi(() => ContactPhotoPicker.PickAsync(card.Model,
            () => Task.FromResult<Stream?>(new MemoryStream(new byte[64], writable: false))))).IsFalse();
        await Assert.That(card.Model.Error).Contains("JPEG"); await Assert.That(card.Model.IsPickingPhoto).IsFalse();
        await Assert.That(card.Model.PhotoBytes).IsNull(); await Assert.That(card.Model.IsDirty).IsFalse();
    }

    [Test, NotInParallel("DesktopUi")]
    public async Task Selecting_identical_valid_photo_clears_a_previous_photo_error_without_another_mutation()
    {
        using var card = await OpenCard(CardDocument()); var bytes = PickerPng();
        await Assert.That(await OnCardUi(() => ContactPhotoPicker.PickAsync(card.Model,
            () => Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false))))).IsTrue();
        var generation = card.Model.Guard.Generation;
        HeadlessRuntime.Dispatch(() => card.Model.ValidationError("Старая ошибка фотографии", "photo"));
        await Assert.That(await OnCardUi(() => ContactPhotoPicker.PickAsync(card.Model,
            () => Task.FromResult<Stream?>(new MemoryStream(bytes, writable: false))))).IsFalse();
        await Assert.That(card.Model.Guard.Generation).IsEqualTo(generation);
        await Assert.That(card.Model.Error).IsEqualTo("");
        await Assert.That(await OnCardUi(() => card.Model.SaveAsync())).IsTrue();
    }

    private static byte[] PickerPng()
    {
        using var bitmap = new SKBitmap(8, 8); bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
    private sealed class DelayedImageStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource<int> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(Environment.CurrentManagedThreadId);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.Text.Json.Nodes;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using TUnit.Core;
using TUnit.Assertions;
using SkiaSharp;

namespace ContactMirror.UiTests.Authoring.Tests;

public abstract partial class RealPipelineScenariosBase<TSession> where TSession : class, IUiTestSession
{
    protected virtual Task StartContactEditVideoAsync() => Task.CompletedTask;
    protected virtual Task FinishContactEditVideoAsync() => Task.CompletedTask;

    [Test, NotInParallel(DesktopUiConstraint)]
    public async Task Whole_contact_edits_and_same_path_photo_survive_snapshot_repair_and_upload()
    {
        Page.WaitUntilNameContains(static p => p.AccountLabel, "Демонстрационный аккаунт")
            .ClickButton(static p => p.CheckChangesButton)
            .WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено")
            .ClickButton(static p => p.ApplyButton)
            .WaitUntilNameContains(static p => p.StatusText, "Синхронизировано", timeoutMs: 15000);
        var workspace = Path.Combine(IntegrationRoot, "workspace");
        var contact = Directory.GetFiles(Path.Combine(workspace, "contacts"), "*.json")
            .Select(path => (Path: path, Doc: JsonNode.Parse(File.ReadAllText(path))!.AsObject()))
            .First(x => x.Doc["photo"] is not null);
        var doc = contact.Doc;
        doc["data"]!["names"]![0]!["familyName"] = "Петренко";
        doc["data"]!["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = "+1 202 555 0199", ["type"] = "home" });
        doc["data"]!["biographies"] = new JsonArray(new JsonObject { ["value"] = "Обновлённая заметка", ["contentType"] = "TEXT_PLAIN" });
        doc["starred"] = !(doc["starred"]?.GetValue<bool>() ?? false);
        doc["google"]!["person"]!["names"]![0]!["familyName"] = "Петренко";
        // A valid alternate PNG at exactly the existing path, not a JSON reference edit.
        using var bitmap = new SKBitmap(96, 96); bitmap.Erase(SKColors.CornflowerBlue);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var paint = new SKPaint { Color = SKColors.PeachPuff, IsAntialias = true };
            canvas.DrawCircle(48, 32, 18, paint); canvas.DrawOval(new SKRect(16, 55, 80, 115), paint);
        }
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        var photo = encoded.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(workspace, doc["photo"]!.GetValue<string>()), photo);
        await File.WriteAllTextAsync(contact.Path, doc.ToJsonString());
        await StartContactEditVideoAsync();
        try
        {
            Page.ClickButton(static p => p.CheckChangesButton)
                .WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено")
                .EnterText(static p => p.SearchInput, "Петренко");
            if (Environment.GetEnvironmentVariable("CONTACTMIRROR_CONTACT_EDIT_BASELINE") == "1") { CaptureCheckpoint("contact-edit-blocked"); return; }
            Page.WaitUntilNameContains(static p => p.DiffSummary, "Фамилия");
            await Task.Delay(500); CaptureCheckpoint("contact-edit-blocked");
            await Assert.That(Page.RepairGoogleSnapshotButton.IsEnabled).IsTrue();
            Page.ClickButton(static p => p.RepairGoogleSnapshotButton)
                .WaitUntilNameContains(static p => p.StatusText, "Правки сохранены");
            Page.WaitUntilNameContains(static p => p.ResultSummary, "Правки сохранены");
            Page.ClickButton(static p => p.CheckChangesButton)
                .WaitUntilNameContains(static p => p.StatusText, "Сравнение завершено");
            CaptureCheckpoint("contact-edit-upload");
            Page.ClickButton(static p => p.ApplyButton)
                .WaitUntilNameContains(static p => p.StatusText, "Синхронизировано", timeoutMs: 20000);
            var current = JsonNode.Parse(await File.ReadAllTextAsync(contact.Path))!;
            await Assert.That(current["data"]!["names"]![0]!["familyName"]!.GetValue<string>()).IsEqualTo("Петренко");
            await Assert.That(await File.ReadAllBytesAsync(Path.Combine(workspace, current["photo"]!.GetValue<string>()))).IsEquivalentTo(photo);
            var remote = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(IntegrationRoot, "remote.json")))!;
            var person = remote["people"]!.AsArray().Single(x => x?["resourceName"]?.ToString() == current["google"]!["resourceName"]!.ToString())!;
            await Assert.That(person["names"]![0]!["familyName"]!.GetValue<string>()).IsEqualTo("Петренко");
            await Assert.That(person["phoneNumbers"]![0]!["value"]!.GetValue<string>()).IsEqualTo("+1 202 555 0199");
            Page.ClickButton(static p => p.CheckChangesButton)
                .WaitUntilNameContains(static p => p.StatusText, "Изменений нет", timeoutMs: 15000);
            CaptureCheckpoint("contact-edit-noop");
        }
        finally { await FinishContactEditVideoAsync(); }
    }
}

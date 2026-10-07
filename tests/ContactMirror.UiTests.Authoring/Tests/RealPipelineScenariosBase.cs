using System.Text.Json;
using System.Text.Json.Nodes;
using AppAutomation.Abstractions;
using AppAutomation.TUnit;
using ContactMirror.UiTests.Authoring.Pages;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Authoring.Tests;

public abstract partial class RealPipelineScenariosBase<TSession> : UiTestBase<TSession, MainWindowPage>
    where TSession : class, IUiTestSession
{
    protected abstract string IntegrationRoot { get; }
    protected virtual void CaptureCheckpoint(string state) { }

    [Test]
    [NotInParallel(DesktopUiConstraint)]
    public async Task Real_demo_pipeline_exports_six_contacts_uploads_disk_edit_and_converges()
    {
        Page.WaitUntilNameContains(static page => page.AccountLabel, "Демонстрационный аккаунт")
            .ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Сравнение завершено");
        CaptureCheckpoint("integrated-initial-download");
        Page.ClickButton(static page => page.ApplyButton)
            .WaitUntilNameContains(static page => page.StatusText, "Синхронизировано", timeoutMs: 15000);

        var contactFiles = Directory.GetFiles(Path.Combine(IntegrationRoot, "workspace", "contacts"), "*.json");
        await Assert.That(contactFiles.Length).IsEqualTo(6);
        var documents = contactFiles.Select(path => (Path: path, Document: JsonNode.Parse(File.ReadAllText(path))!.AsObject())).ToArray();
        var contact = documents.Single(item => item.Document["google"]?["resourceName"]?.GetValue<string>() == "people/demo2");
        const string newPhone = "+1 202 555 0199";
        contact.Document["data"]!["phoneNumbers"]![0]!["value"] = newPhone;
        await File.WriteAllTextAsync(contact.Path, contact.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Page.ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Сравнение завершено")
            .WaitUntilNameContains(static page => page.ApplyButton, "1");
        CaptureCheckpoint("integrated-local-phone-upload");
        Page.ClickButton(static page => page.ApplyButton)
            .WaitUntilNameContains(static page => page.StatusText, "Синхронизировано", timeoutMs: 15000);

        var remote = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(IntegrationRoot, "remote.json")))!;
        var remotePerson = remote["people"]!.AsArray().Single(person => person?["resourceName"]?.GetValue<string>() == "people/demo2")!;
        await Assert.That(remotePerson["phoneNumbers"]![0]!["value"]!.GetValue<string>()).IsEqualTo(newPhone);

        Page.ClickButton(static page => page.CheckChangesButton)
            .WaitUntilNameContains(static page => page.StatusText, "Изменений нет", timeoutMs: 15000);
        CaptureCheckpoint("integrated-converged-noop");
        await Assert.That(Page.ApplyButton.IsEnabled).IsFalse();
    }
}

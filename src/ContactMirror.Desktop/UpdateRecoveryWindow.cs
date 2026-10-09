using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ContactMirror.Desktop;

public sealed class UpdateRecoveryWindow : Window
{
    public UpdateRecoveryWindow()
    {
        Icon = App.LoadWindowIcon();
        Title = "ContactMirror · установка"; Width = 600; Height = 320; MinWidth = 420; MinHeight = 260;
        var message = new TextBlock { Text = Program.StartupProblem, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var recover = new Button { Content = "Проверить восстановление", IsVisible = Program.CanRecoverUpdate, HorizontalAlignment = HorizontalAlignment.Left };
        recover.Click += (_, _) =>
        {
            try
            {
                if (!Program.RecoverUpdate()) { message.Text = "Безопасное завершение обновления пока не подтверждено. Закройте это окно, дождитесь установщика или повторно запустите подготовленный Setup."; return; }
                Program.RestartAfterRecovery = true;
                Close();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
            { message.Text = "Не удалось восстановить запуск. Повторно запустите подготовленный Setup; файлы контактов остаются в вашей папке."; }
        };
        var close = new Button { Content = "Закрыть", HorizontalAlignment = HorizontalAlignment.Left }; close.Click += (_, _) => Close();
        Content = new StackPanel { Margin = new Thickness(28), Spacing = 18, Children = { new TextBlock { Text = "Состояние установки", FontSize = 22 }, message, recover, close } };
    }
}

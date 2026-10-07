using ContactMirror.Application;
using ContactMirror.Core;

namespace ContactMirror.Infrastructure.Demo;

public sealed class DemoAccountConnector : IAccountConnector
{
    public static AccountIdentity Identity { get; } = new("contactmirror-demo", "Демонстрационный аккаунт", "contactmirror://demo");
    private bool connected = true;
    public bool IsConfigured => true;
    public string ConfigurationHint => "Демонстрационный режим: вымышленные контакты, данные остаются на компьютере. Подключение и синхронизация Google не выполняются.";
    public Task<AccountIdentity?> GetAccountAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(connected ? Identity : null); }
    public Task<AccountIdentity> SignInAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); connected = true; return Task.FromResult(Identity); }
    public Task SignOutAsync(bool revoke = false, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); connected = false; return Task.CompletedTask; }
}

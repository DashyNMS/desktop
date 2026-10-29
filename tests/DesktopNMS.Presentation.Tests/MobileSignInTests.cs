using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Security;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using DesktopNMS.ViewModels;
using Xunit;

namespace DesktopNMS.Presentation.Tests;

/// <summary>Settings › Server's QR code for signing DashyNMS Mobile in (#263).</summary>
public sealed class MobileSignInTests
{
    private const string Token = "12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd";

    private static LibreNmsConnection Connection(string token = Token) =>
        new(new Uri("https://nms.example.com/"), token, backupWebRoot: new Uri("https://192.0.2.20/"));

    [Fact]
    public void The_code_stays_hidden_until_asked_for()
    {
        var mobile = new MobileSignInViewModel(new Session(Connection()), timers: _ => new Timer());

        Assert.True(mobile.IsAvailable);
        Assert.True(mobile.IsHidden);
        Assert.Null(mobile.Code);
        Assert.True(mobile.ShowCommand.CanExecute(null));
        Assert.False(mobile.HideCommand.CanExecute(null));
    }

    [Fact]
    public void Showing_it_starts_the_minute_and_the_minute_hides_it()
    {
        var timer = new Timer();
        var mobile = new MobileSignInViewModel(new Session(Connection()), timers: interval => { Assert.Equal(TimeSpan.FromMinutes(1), interval); return timer; });

        mobile.ShowCommand.Execute(null);

        Assert.True(mobile.IsShown);
        Assert.NotNull(mobile.Code);
        Assert.True(timer.IsEnabled);
        Assert.False(mobile.ShowCommand.CanExecute(null));

        timer.Fire();

        Assert.True(mobile.IsHidden);
        Assert.Null(mobile.Code);
        Assert.False(timer.IsEnabled);
    }

    [Fact]
    public void The_code_is_the_sign_in_link_and_differs_from_the_stand_in()
    {
        var mobile = new MobileSignInViewModel(new Session(Connection()), timers: _ => new Timer());
        mobile.Show();

        var expected = QrMatrix.Encode(SignInLink.Create(Connection()).ToString());
        Assert.Equal(expected.Size, mobile.Code!.Size);
        Assert.All(Enumerable.Range(0, expected.Size), row =>
            Assert.All(Enumerable.Range(0, expected.Size), column => Assert.Equal(expected.IsDark(row, column), mobile.Code.IsDark(row, column))));
        Assert.Contains(Enumerable.Range(0, Math.Min(expected.Size, mobile.StandIn.Size)),
            row => Enumerable.Range(0, Math.Min(expected.Size, mobile.StandIn.Size)).Any(column => expected.IsDark(row, column) != mobile.StandIn.IsDark(row, column)));
    }

    [Fact]
    public void Nothing_to_show_while_signed_out_or_in_demo_mode()
    {
        var signedOut = new MobileSignInViewModel(new Session(null), timers: _ => new Timer());
        var demo = new MobileSignInViewModel(new Session(Connection()), demoMode: true, timers: _ => new Timer());

        foreach (var mobile in new[] { signedOut, demo })
        {
            Assert.False(mobile.IsAvailable);
            Assert.False(mobile.ShowCommand.CanExecute(null));
            mobile.Show();
            Assert.Null(mobile.Code);
        }
    }

    [Fact]
    public void A_token_Core_wont_put_in_a_link_says_so_instead()
    {
        var mobile = new MobileSignInViewModel(new Session(Connection("not a token")), timers: _ => new Timer());

        mobile.Show();

        Assert.Null(mobile.Code);
        Assert.True(mobile.HasError);
    }

    [Fact]
    public void A_change_of_connection_hides_the_code()
    {
        var session = new Session(Connection());
        var mobile = new MobileSignInViewModel(session, timers: _ => new Timer());
        mobile.Show();

        session.Connection = null;
        session.Raise();

        Assert.Null(mobile.Code);
        Assert.False(mobile.IsAvailable);
    }

    [Fact]
    public void Closing_settings_hides_it_and_lets_go()
    {
        var session = new Session(Connection());
        var timer = new Timer();
        var mobile = new MobileSignInViewModel(session, timers: _ => timer);
        mobile.Show();

        mobile.Detach();

        Assert.Null(mobile.Code);
        Assert.False(timer.IsEnabled);
        Assert.False(session.HasListeners);
    }

    private sealed class Session(LibreNmsConnection? connection) : ISessionService
    {
        public LibreNmsConnection? Connection { get; set; } = connection;
        public SystemInfo? ServerInfo => null;
        public bool IsConnected => Connection is not null;
        public event EventHandler? StateChanged;
        public bool HasListeners => StateChanged is not null;
        public void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);
        public Task<ConnectionTestResult> SignInAsync(string serverUrl, string apiToken, bool allowUntrustedCertificate, bool rememberToken, CancellationToken cancellationToken = default, string? backupAddress = null) => throw new NotSupportedException();
        public Task<ConnectionTestResult> ReconnectAsync(string serverUrl, string? newApiToken, bool allowUntrustedCertificate, string? backupAddress, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void TrustCertificate(CertificateDetails certificate) => throw new NotSupportedException();
        public Task<ConnectionTestResult?> TryRestoreAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void SignOut(bool forgetToken) => throw new NotSupportedException();
    }

    private sealed class Timer : IUiTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsEnabled { get; private set; }
        public event EventHandler? Tick;
        public void Start() => IsEnabled = true;
        public void Stop() => IsEnabled = false;
        public void Fire() { if (IsEnabled) Tick?.Invoke(this, EventArgs.Empty); }
    }
}

using System;
using DesktopNMS.Core.SignIn;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using QRCoder;

namespace DesktopNMS.ViewModels;

/// <summary>The modules of a QR code, quiet zone included, for the view to draw.</summary>
public sealed class QrMatrix
{
    private readonly bool[] _dark;

    public QrMatrix(int size, bool[] dark)
    {
        if (dark.Length != size * size)
        {
            throw new ArgumentException("The modules don't make a square of that size.", nameof(dark));
        }

        Size = size;
        _dark = dark;
    }

    /// <summary>Modules along each side.</summary>
    public int Size { get; }

    private static readonly Lazy<QrMatrix> StandInCode = new(() => Encode("dashynms://sign-in"));

    /// <summary>
    /// A code the size of a real one with nothing in it, shown blurred while
    /// the real one is hidden - so the card looks the same without the token
    /// ever being on screen.
    /// </summary>
    public static QrMatrix StandIn => StandInCode.Value;

    public bool IsDark(int row, int column) => _dark[(row * Size) + column];

    /// <summary>Encodes <paramref name="text"/> at medium error correction, which a phone camera reads easily off a screen.</summary>
    public static QrMatrix Encode(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var dark = new bool[size * size];
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                dark[(row * size) + column] = rows[row][column];
            }
        }

        return new QrMatrix(size, dark);
    }
}

/// <summary>
/// Settings › Server's "Sign in DashyNMS Mobile" (#263): a QR code holding a
/// Core <see cref="SignInLink"/> for the phone's Camera app to open.
/// </summary>
/// <remarks>
/// The code carries the API token, so it's treated as a password: built only
/// when the user asks to see it, and dropped again after
/// <see cref="ShowFor"/>, when they leave the section or close Settings, or
/// when the connection changes. While hidden the view shows a blurred
/// stand-in, never the real code blurred, which a camera might still read.
/// </remarks>
public sealed class MobileSignInViewModel : ObservableObject
{
    /// <summary>Long enough to fetch a phone and scan, short enough not to linger on screen.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromMinutes(1);

    private readonly ISessionService _session;
    private readonly bool _demoMode;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUiTimer _hideTimer;
    private QrMatrix? _code;
    private string? _error;

    public MobileSignInViewModel(ISessionService session, bool demoMode = false, Func<TimeSpan, IUiTimer>? timers = null, IUiDispatcher? dispatcher = null)
    {
        _session = session;
        _demoMode = demoMode;
        _dispatcher = dispatcher ?? UiThread.Dispatcher;
        _hideTimer = (timers ?? (interval => UiThread.CreateTimer(interval)))(ShowFor);
        _hideTimer.Tick += OnHideTimerTick;
        _session.StateChanged += OnSessionStateChanged;
        ShowCommand = new RelayCommand(Show, () => IsAvailable && !IsShown);
        HideCommand = new RelayCommand(Hide, () => IsShown);
    }

    public RelayCommand ShowCommand { get; }

    public RelayCommand HideCommand { get; }

    /// <summary>Signed in, so there's a server and token to hand over. Demo mode has nothing real to share.</summary>
    public bool IsAvailable => !_demoMode && _session.Connection is not null;

    public bool IsUnavailable => !IsAvailable;

    /// <summary>The real code, only while shown.</summary>
    public QrMatrix? Code
    {
        get => _code;
        private set
        {
            if (SetProperty(ref _code, value))
            {
                OnPropertyChanged(nameof(IsShown));
                OnPropertyChanged(nameof(IsHidden));
                ShowCommand.RaiseCanExecuteChanged();
                HideCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public QrMatrix StandIn => QrMatrix.StandIn;

    public bool IsShown => Code is not null;

    public bool IsHidden => Code is null;

    /// <summary>Why the code couldn't be made, for example a token in an older format Core can't vouch for.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>The server the phone will sign in to, for beside the code.</summary>
    public string ServerText => _session.Connection?.WebRoot.AbsoluteUri ?? string.Empty;

    public void Show()
    {
        if (!IsAvailable || _session.Connection is not { } connection)
        {
            return;
        }

        try
        {
            Code = QrMatrix.Encode(SignInLink.Create(connection).ToString());
            Error = null;
        }
        catch (ArgumentException)
        {
            Code = null;
            Error = "This sign-in can't be shared as a QR code. Sign in on the phone with an API token instead.";
            return;
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Hide()
    {
        _hideTimer.Stop();
        Code = null;
    }

    /// <summary>Settings is closing: hide the code and let go of the session.</summary>
    public void Detach()
    {
        Hide();
        _hideTimer.Tick -= OnHideTimerTick;
        _session.StateChanged -= OnSessionStateChanged;
    }

    private void OnHideTimerTick(object? sender, EventArgs e) => Hide();

    private void OnSessionStateChanged(object? sender, EventArgs e) => _dispatcher.Post(() =>
    {
        // A code for the old connection would sign the phone in somewhere else.
        Hide();
        Error = null;
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(ServerText));
        ShowCommand.RaiseCanExecuteChanged();
    });
}

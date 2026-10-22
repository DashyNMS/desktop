using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.Views.Intelligence;

/// <summary>
/// Dashy, a small pixel companion above the question box (#250, from the WPF
/// app's IntelligenceMascot). Bobs while an answer is coming, waves when one
/// arrives or when clicked. Greetings are local and never send a chat message.
/// Drawn cell by cell - whole 4px squares, no smoothing - and moved in
/// discrete frames, so the pixel grid holds even mid-animation.
/// </summary>
public sealed class IntelligenceMascot : Control
{
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<IntelligenceMascot, bool>(nameof(IsBusy));

    public static readonly StyledProperty<bool> HasErrorProperty =
        AvaloniaProperty.Register<IntelligenceMascot, bool>(nameof(HasError));

    public static readonly StyledProperty<DashyOutfit> OutfitProperty =
        AvaloniaProperty.Register<IntelligenceMascot, DashyOutfit>(nameof(Outfit));

    private static readonly string[] Sprite =
    [
        ".....KKKKKK.....",
        "....KCCCCCCK....",
        "...KCHCCCCCCK...",
        "...KCCCCCCCCK...",
        "..KCCWWCCWWCCK..",
        "..KCWKWCCWKWCK..",
        "..KCWWWCCWWWCK..",
        "..KCCWWOOWWCCK..",
        "..KCCWOOOOWCCK..",
        ".KKCCWWOOWWCCK..",
        "KCKCCWWWWWWCCK..",
        "KCKCWWWWWWWWCK..",
        "KCKCWWWWWWWWCK..",
        ".KKCWWWWWWWWCK..",
        "..KCWWWWWWWWCK..",
        "..KCCWWWWWWCCK..",
        "...KCCWWWWCCK...",
        "....KKKKKKKK....",
        "...OOOOKKOOOO...",
        "..OOOOO..OOOOO..",
    ];

    private static readonly string[] RestingFlipper = ["KK.", "CCK", "CCK", "CCK", "CCK", "CK.", "K.."];

    // Two bent-elbow poses: the hand waves beside the cheek while the upper
    // flipper stays tucked in. Never rotate a straight arm upwards.
    private static readonly string[] WaveA = ["..KK.", "..CCK", "..CCK", ".KCCK", "KCCC.", "CCC..", "CC...", "K...."];
    private static readonly string[] WaveB = ["KK...", "CCK..", "KCCK.", ".CCCK", "KCCC.", "CCC..", "CC...", "K...."];

    /// <summary>One frame of a bob or wave: up, back, still.</summary>
    private static readonly TimeSpan FrameLength = TimeSpan.FromMilliseconds(200);

    private readonly DispatcherTimer _frames;
    private readonly DispatcherTimer _greeting = new() { Interval = TimeSpan.FromSeconds(2) };
    private int _frame;
    private int _framesLeft;
    private bool _waving;
    private bool _happy;

    public IntelligenceMascot()
    {
        Width = 40;
        Height = 44;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Say hello to Dashy");
        AutomationProperties.SetName(this, "Dashy Companion: smile and wave");
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        _frames = new DispatcherTimer(FrameLength, DispatcherPriority.Render, (_, _) => NextFrame());
        _greeting.Tick += (_, _) =>
        {
            _greeting.Stop();
            _happy = false;
            UpdateAnimation();
        };
    }

    public bool IsBusy { get => GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }

    public bool HasError { get => GetValue(HasErrorProperty); set => SetValue(HasErrorProperty, value); }

    public DashyOutfit Outfit { get => GetValue(OutfitProperty); set => SetValue(OutfitProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsBusyProperty)
        {
            // An answer just finished: wave hello, unless it went wrong.
            if (change.GetOldValue<bool>() && !change.GetNewValue<bool>() && !HasError)
            {
                Greet();
            }
            else
            {
                UpdateAnimation();
            }
        }
        else if (change.Property == HasErrorProperty)
        {
            UpdateAnimation();
        }
        else if (change.Property == OutfitProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateAnimation();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            Greet();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space)
        {
            Greet();
            e.Handled = true;
        }
    }

    private void Stop()
    {
        _frames.Stop();
        _greeting.Stop();
        _frame = 0;
        _waving = false;
        _happy = false;
        InvalidateVisual();
    }

    private void Greet()
    {
        Stop();
        if (!IsEffectivelyVisible || VisualRoot is null)
        {
            return;
        }

        _happy = true;
        _waving = true;
        _framesLeft = 9; // three rounds of up, back, still
        _greeting.Start();
        _frames.Start();
        InvalidateVisual();
    }

    private void UpdateAnimation()
    {
        if (_greeting.IsEnabled)
        {
            return;
        }

        Stop();
        if (IsBusy && IsEffectivelyVisible && VisualRoot is not null)
        {
            _framesLeft = -1; // until the answer's in
            _frames.Start();
        }
    }

    private void NextFrame()
    {
        _frame = (_frame + 1) % 3;
        if (_framesLeft > 0 && --_framesLeft == 0)
        {
            _frames.Stop();
            _frame = 0;
            _waving = false;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // The drawing is 80x88 in its own 4px-cell units, shown at half size.
        using var scale = context.PushTransform(Matrix.CreateScale(Bounds.Width / 80, Bounds.Height / 88));

        Fill(context, 24, 82, 32, 2, Color.FromArgb(35, 100, 116, 139));

        // The body bobs up a cell on the middle frame.
        var lift = _frame == 1 ? -4 : 0;
        using var body = context.PushTransform(Matrix.CreateTranslation(8, 2 + lift));

        Paint(context, Sprite, 0, 0);

        if (_waving)
        {
            Paint(context, _frame == 1 ? WaveB : WaveA, 52, 20);
        }
        else
        {
            using var tilt = context.PushTransform(Matrix.CreateTranslation(-56, -40) * Matrix.CreateRotation(-10 * Math.PI / 180) * Matrix.CreateTranslation(56, 40));
            Paint(context, RestingFlipper, 52, 36);
        }

        if (_happy)
        {
            // Smiling eyes and a tiny beak-side grin replace the resting expression.
            foreach (var x in new[] { 16.0, 40.0 })
            {
                Fill(context, x, 20, 8, 8, ColourFor('W'));
                Fill(context, x, 22, 2, 4, ColourFor('K'));
                Fill(context, x + 2, 20, 4, 2, ColourFor('K'));
                Fill(context, x + 6, 22, 2, 4, ColourFor('K'));
            }

            Fill(context, 26, 38, 2, 2, ColourFor('K'));
            Fill(context, 28, 40, 8, 2, ColourFor('K'));
            Fill(context, 36, 38, 2, 2, ColourFor('K'));
        }

        switch (Outfit)
        {
            case DashyOutfit.Bowtie:
                Paint(context, ["B...B", "BBRBB", "BBRBB", "B...B"], 22, 44);
                break;
            case DashyOutfit.Fedora:
                Paint(context, ["..FFFFF...", ".FFFFFFF..", ".FDDDDDF..", "FFFFFFFFFF"], 12, -8);
                break;
            case DashyOutfit.Wizard:
                Paint(context, [".....P....", "....PP....", "...PPPL...", "...PPPL...", "..PPPPPL..", ".PPPSPPPL.", "PPPPPPPPPP"], 12, -20);
                Paint(context, ["PP.......PP", "PL.......LP", "PL.......LP", "PP.......PP", "PP.......PP", "PP.......PP", "PPP.....PPP", "PPPPPPPPPPP"], 10, 36);
                Fill(context, 30, 44, 4, 4, ColourFor('S'));
                break;
        }
    }

    private static void Paint(DrawingContext context, string[] rows, double left, double top)
    {
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < rows[y].Length; x++)
            {
                if (rows[y][x] != '.')
                {
                    Fill(context, left + (x * 4), top + (y * 4), 4, 4, ColourFor(rows[y][x]));
                }
            }
        }
    }

    private static void Fill(DrawingContext context, double x, double y, double width, double height, Color colour) =>
        context.FillRectangle(new ImmutableSolidColorBrush(colour), new Rect(x, y, width, height));

    private static Color ColourFor(char pixel) => pixel switch
    {
        'K' => Color.FromRgb(0x35, 0x46, 0x5D),
        'C' => Color.FromRgb(0x19, 0x25, 0x38),
        'H' => Color.FromRgb(0x52, 0x6B, 0x86),
        'W' => Color.FromRgb(0xF1, 0xF5, 0xF9),
        'B' => Color.FromRgb(0xD9, 0x3E, 0x65),
        'R' => Color.FromRgb(0xFF, 0x9E, 0xB1),
        'F' => Color.FromRgb(0x92, 0x64, 0x44),
        'D' => Color.FromRgb(0x56, 0x3B, 0x31),
        'P' => Color.FromRgb(0x7C, 0x4E, 0xD8),
        'L' => Color.FromRgb(0xB6, 0x9C, 0xFC),
        'S' => Color.FromRgb(0xF6, 0xD7, 0x7B),
        _ => Color.FromRgb(0xFF, 0xB8, 0x47),
    };
}

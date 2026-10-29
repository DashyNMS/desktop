using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DesktopNMS.ViewModels;

namespace DesktopNMS.Views;

/// <summary>
/// Draws a <see cref="QrMatrix"/> as square modules, dark on white in either
/// theme, since phone cameras read that most reliably (#263).
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<QrMatrix?> CodeProperty =
        AvaloniaProperty.Register<QrCodeView, QrMatrix?>(nameof(Code));

    private static readonly IBrush Light = new ImmutableSolidColorBrush(Colors.White);
    private static readonly IBrush Dark = new ImmutableSolidColorBrush(Colors.Black);

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(CodeProperty);
    }

    public QrMatrix? Code { get => GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (Code is not { Size: > 0 } code || side <= 0)
        {
            return;
        }

        // Whole pixels per module keep the edges crisp for the camera.
        var module = Math.Max(1, Math.Floor(side / code.Size));
        var drawn = module * code.Size;
        var left = Math.Floor((Bounds.Width - drawn) / 2);
        var top = Math.Floor((Bounds.Height - drawn) / 2);

        context.FillRectangle(Light, new Rect(left, top, drawn, drawn));
        for (var row = 0; row < code.Size; row++)
        {
            for (var column = 0; column < code.Size; column++)
            {
                if (code.IsDark(row, column))
                {
                    context.FillRectangle(Dark, new Rect(left + (column * module), top + (row * module), module, module));
                }
            }
        }
    }
}

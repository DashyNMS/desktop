using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;
using DesktopNMS.Core.Services;
using FluentIcons.Avalonia;
using FluentIcons.Common;

namespace DesktopNMS.Views;

/// <summary>
/// The device details header's tile (#555): the device's own logo once it has
/// loaded, on a tile in the theme's surface tones, and the device-type symbol
/// until then - or when there's no logo, or its file can't be read - so the
/// header never goes blank or jumps.
/// </summary>
public sealed class DeviceLogoTile : Panel
{
    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<DeviceLogoTile, Symbol>(nameof(Symbol));

    public static readonly StyledProperty<DeviceLogo?> LogoProperty =
        AvaloniaProperty.Register<DeviceLogoTile, DeviceLogo?>(nameof(Logo));

    private readonly SymbolIcon _symbol;
    private readonly Border _symbolTile;
    private readonly Image _image;
    private readonly Border _logoTile;

    public DeviceLogoTile()
    {
        _symbol = new SymbolIcon { FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _symbol.Bind(SymbolIcon.ForegroundProperty, this.GetResourceObservable("AccentBrush"));
        _symbolTile = new Border { CornerRadius = new CornerRadius(12), Child = _symbol };
        _symbolTile.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSubtleBrush"));

        // Padding keeps a logo off the tile's edge; Uniform keeps its shape.
        _image = new Image { Stretch = Stretch.Uniform };
        _logoTile = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(7), BorderThickness = new Thickness(1), Child = _image, IsVisible = false };
        _logoTile.Bind(Border.BackgroundProperty, this.GetResourceObservable("LogoTileBrush"));
        _logoTile.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderBrush"));

        Children.Add(_symbolTile);
        Children.Add(_logoTile);
    }

    public Symbol Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }

    /// <summary>The cached logo file to show, or null for the symbol.</summary>
    public DeviceLogo? Logo { get => GetValue(LogoProperty); set => SetValue(LogoProperty, value); }

    /// <summary>Whether the logo is showing rather than the symbol.</summary>
    public bool ShowsLogo => _logoTile.IsVisible;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SymbolProperty)
        {
            _symbol.Symbol = Symbol;
        }
        else if (change.Property == LogoProperty)
        {
            _image.Source = Load(Logo);
            _logoTile.IsVisible = _image.Source is not null;
            _symbolTile.IsVisible = !_logoTile.IsVisible;
        }
    }

    /// <summary>
    /// The logo as an image: an SVG through the same Skia path as
    /// Converters.SvgToImage, a PNG as a bitmap. Read whole and closed
    /// straight away, since Core may evict the file later. Null when it
    /// can't be read.
    /// </summary>
    internal static IImage? Load(DeviceLogo? logo)
    {
        if (logo is null)
        {
            return null;
        }

        try
        {
            if (logo.Format == DeviceLogoFormat.Png)
            {
                using var stream = new MemoryStream(File.ReadAllBytes(logo.FilePath));
                return new Bitmap(stream);
            }

            return SvgSource.LoadFromSvg(File.ReadAllText(logo.FilePath)) is { } source ? new SvgImage { Source = source } : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

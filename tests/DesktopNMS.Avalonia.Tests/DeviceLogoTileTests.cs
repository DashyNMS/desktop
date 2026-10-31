using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DesktopNMS.Core.Services;
using DesktopNMS.Views;
using FluentIcons.Avalonia;
using FluentIcons.Common;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

/// <summary>The device details header's tile (#555): the type symbol until a logo arrives, then the logo.</summary>
public sealed class DeviceLogoTileTests : IDisposable
{
    /// <summary>A 1x1 transparent PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-logo-tile-" + Guid.NewGuid().ToString("N"));

    public DeviceLogoTileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [AvaloniaFact]
    public void The_symbol_shows_until_the_logo_arrives_and_the_logo_after()
    {
        var path = Path.Combine(_folder, "aruba.png");
        File.WriteAllBytes(path, Png);
        var tile = new DeviceLogoTile { Width = 48, Height = 48, Symbol = Symbol.Router };
        var window = new Window { Content = tile };
        window.Show();
        try
        {
            Assert.False(tile.ShowsLogo);
            Assert.True(SymbolOf(tile).IsEffectivelyVisible);
            Assert.Equal(Symbol.Router, SymbolOf(tile).Symbol);

            tile.Logo = new DeviceLogo(path, DeviceLogoFormat.Png);

            Assert.True(tile.ShowsLogo);
            Assert.False(SymbolOf(tile).IsEffectivelyVisible);
            Assert.NotNull(Assert.Single(tile.GetVisualDescendants().OfType<Image>()).Source);

            tile.Logo = null;

            Assert.False(tile.ShowsLogo);
            Assert.True(SymbolOf(tile).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_logo_file_that_cannot_be_read_keeps_the_symbol()
    {
        var path = Path.Combine(_folder, "gone.svg");
        var tile = new DeviceLogoTile { Width = 48, Height = 48, Symbol = Symbol.Router };
        var window = new Window { Content = tile };
        window.Show();
        try
        {
            tile.Logo = new DeviceLogo(path, DeviceLogoFormat.Svg);

            Assert.False(tile.ShowsLogo);
            Assert.True(SymbolOf(tile).IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static SymbolIcon SymbolOf(DeviceLogoTile tile) => Assert.Single(tile.GetVisualDescendants().OfType<SymbolIcon>());
}

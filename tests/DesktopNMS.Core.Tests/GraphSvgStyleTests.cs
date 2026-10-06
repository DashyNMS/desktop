using DesktopNMS.Core.Graphs;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Against SVG shaped exactly as rrdtool 1.11 writes it for LibreNMS's graphs
/// (attribute colours as rgb percentages, text as glyph groups) - cut down
/// from renders of LibreNMS's own ping and port traffic definitions.
/// </summary>
public class GraphSvgStyleTests
{
    private static readonly GraphPalette Palette = GraphPalette.Dark;

    private const string Header = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1137\" height=\"433\" viewBox=\"0 0 1137 433\">\n";

    private const string MinorGrid = "<path fill=\"none\" stroke-width=\"0.4\" stroke-linecap=\"butt\" stroke-linejoin=\"miter\" stroke=\"rgb(64.705882%, 64.705882%, 64.705882%)\" stroke-opacity=\"1\" stroke-dasharray=\"1 1\" stroke-miterlimit=\"10\" d=\"M 74 366 L 74 14 \"/>";
    private const string MajorGridHorizontal = "<path fill=\"none\" stroke-width=\"0.6\" stroke-linecap=\"butt\" stroke-linejoin=\"miter\" stroke=\"rgb(100%, 60%, 60%)\" stroke-opacity=\"1\" stroke-dasharray=\"1 1\" stroke-miterlimit=\"10\" d=\"M 61 300 L 1065 300 \"/>";
    private const string MajorGridVertical = "<path fill=\"none\" stroke-width=\"0.6\" stroke-linecap=\"butt\" stroke-linejoin=\"miter\" stroke=\"rgb(100%, 60%, 60%)\" stroke-opacity=\"1\" stroke-dasharray=\"1 1\" stroke-miterlimit=\"10\" d=\"M 200 366 L 200 14 \"/>";
    private const string Tick = "<path fill=\"none\" stroke-width=\"0.6\" stroke-linecap=\"butt\" stroke-linejoin=\"miter\" stroke=\"rgb(100%, 60%, 60%)\" stroke-opacity=\"1\" stroke-miterlimit=\"10\" d=\"M 95 13 L 95 15 \"/>";
    private const string AxisLine = "<path fill=\"none\" stroke-width=\"0.6\" stroke-linecap=\"butt\" stroke-linejoin=\"miter\" stroke=\"rgb(12%, 12%, 12%)\" stroke-opacity=\"1\" stroke-miterlimit=\"10\" d=\"M 57 365 L 1065 365 \"/>";
    private const string Text = "<g fill=\"rgb(0%, 0%, 0%)\" fill-opacity=\"1\">\n<use xlink:href=\"#glyph-0-0\" x=\"115\" y=\"378\"/>\n</g>";

    // Ping: jitter band (#ccd2de at 0.8), the RTT line (#36393d), a loss bar (#d42e08) and the RTT legend swatch.
    private const string JitterBand = "<path fill-rule=\"nonzero\" fill=\"rgb(80%, 82.352941%, 87.058824%)\" fill-opacity=\"0.8\" d=\"M 61 325 L 61 349 L 64 349 L 64 330 Z M 61 325 \"/>";
    private const string RttLine = "<path fill=\"none\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"rgb(21.176471%, 22.352941%, 23.921569%)\" stroke-opacity=\"1\" stroke-miterlimit=\"10\" d=\"M 61 339 L 64 339 L 64 338 L 67 338 \"/>";
    private const string LossBar = "<path fill-rule=\"nonzero\" fill=\"rgb(83.137255%, 18.039216%, 3.137255%)\" fill-opacity=\"1\" d=\"M 140 56 L 140 365 L 147 365 L 147 358 L 143 358 L 143 56 Z M 140 56 \"/>";
    private const string RttSwatch = "<path fill-rule=\"nonzero\" fill=\"rgb(21.176471%, 22.352941%, 23.921569%)\" fill-opacity=\"1\" d=\"M 16 401.921875 L 16 409.121094 L 23.199219 409.121094 L 23.199219 401.921875 Z M 16 401.921875 \"/>";

    // Port traffic: In area #91B13C and line #006600, Out area #8080BD and line #000099.
    private const string InArea = "<path fill-rule=\"nonzero\" fill=\"rgb(56.862745%, 69.411765%, 23.529412%)\" fill-opacity=\"1\" d=\"M 61 200 L 61 274 L 70 274 L 70 190 Z M 61 200 \"/>";
    private const string InLine = "<path fill=\"none\" stroke-width=\"1.25\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"rgb(0%, 40%, 0%)\" stroke-opacity=\"1\" stroke-miterlimit=\"10\" d=\"M 61 200 L 70 190 \"/>";
    private const string OutArea = "<path fill-rule=\"nonzero\" fill=\"rgb(50.196078%, 50.196078%, 74.117647%)\" fill-opacity=\"1\" d=\"M 61 274 L 61 320 L 70 320 L 70 274 Z M 61 274 \"/>";
    private const string OutLine = "<path fill=\"none\" stroke-width=\"1.25\" stroke-linecap=\"round\" stroke-linejoin=\"round\" stroke=\"rgb(0%, 0%, 60%)\" stroke-opacity=\"1\" stroke-miterlimit=\"10\" d=\"M 61 320 L 70 318 \"/>";

    private static string Svg(params string[] parts) => Header + string.Join("\n", parts) + "\n</svg>\n";

    [Fact]
    public void Minor_and_vertical_grid_and_ticks_go_and_horizontal_major_grid_is_dashed_in_the_grid_colour()
    {
        var styled = GraphSvgStyle.Apply(Svg(MinorGrid, MajorGridHorizontal, MajorGridVertical, Tick), Palette);

        Assert.DoesNotContain("64.705882%", styled);
        Assert.DoesNotContain("M 200 366", styled);
        Assert.DoesNotContain("M 95 13", styled);
        Assert.Contains($"stroke=\"{Palette.Grid}\"", styled);
        Assert.Contains("stroke-dasharray=\"3 5\"", styled);
        Assert.Contains("M 61 300 L 1065 300", styled);
    }

    [Fact]
    public void Text_and_axis_take_the_muted_text_and_grid_colours()
    {
        var styled = GraphSvgStyle.Apply(Svg(AxisLine, Text), Palette);

        Assert.Contains($"<g fill=\"{Palette.Text}\"", styled);
        Assert.Contains($"stroke=\"{Palette.Grid}\"", styled);
        Assert.DoesNotContain("rgb(0%, 0%, 0%)", styled);
        Assert.DoesNotContain("rgb(12%, 12%, 12%)", styled);
    }

    [Fact]
    public void Ping_is_the_accent_with_a_see_through_band_and_loss_stays_red()
    {
        var styled = GraphSvgStyle.Apply(Svg(JitterBand, RttLine, LossBar, RttSwatch), Palette);
        var lines = styled.Split('\n');

        var band = lines.Single(l => l.Contains("M 61 325"));
        Assert.Contains($"fill=\"{Palette.Accent}\"", band);
        Assert.Contains("fill-opacity=\"0.22\"", band);

        Assert.Contains($"stroke=\"{Palette.Accent}\"", lines.Single(l => l.Contains("M 61 339")));
        Assert.Contains($"fill=\"{Palette.Critical}\"", lines.Single(l => l.Contains("M 140 56")));

        // The legend's colour square stays solid.
        var swatch = lines.Single(l => l.Contains("M 16 401.921875"));
        Assert.Contains($"fill=\"{Palette.Accent}\"", swatch);
        Assert.Contains("fill-opacity=\"1\"", swatch);
    }

    [Fact]
    public void Traffic_is_in_blue_and_out_green_as_in_the_mockups_with_lines_thickened()
    {
        var styled = GraphSvgStyle.Apply(Svg(InArea, InLine, OutArea, OutLine), Palette);
        var lines = styled.Split('\n');

        Assert.Contains($"fill=\"{Palette.Accent}\"", lines.Single(l => l.Contains("M 61 200 L 61 274")));
        var inLine = lines.Single(l => l.Contains("M 61 200 L 70 190"));
        Assert.Contains($"stroke=\"{Palette.Accent}\"", inLine);
        Assert.Contains("stroke-width=\"1.6\"", inLine);

        Assert.Contains($"fill=\"{Palette.Ok}\"", lines.Single(l => l.Contains("M 61 274 L 61 320")));
        Assert.Contains($"stroke=\"{Palette.Ok}\"", lines.Single(l => l.Contains("M 61 320 L 70 318")));
    }

    [Fact]
    public void The_first_series_takes_the_accent_even_when_red()
    {
        // LibreNMS's CPU graphs start with #CC0000 - not an alarm.
        const string cpu = "<path fill=\"none\" stroke-width=\"1.25\" stroke=\"rgb(80%, 0%, 0%)\" stroke-opacity=\"1\" d=\"M 61 300 L 70 290 \"/>";

        Assert.Contains($"stroke=\"{Palette.Accent}\"", GraphSvgStyle.Apply(Svg(cpu), Palette));
    }

    [Fact]
    public void A_blue_series_after_a_red_first_one_is_not_red()
    {
        // A multi-line graph as a real server sent it: #CC0000, then #4096EE, then #008C00.
        const string red = "<path fill=\"none\" stroke-width=\"1.25\" stroke=\"rgb(80%, 0%, 0%)\" stroke-opacity=\"1\" d=\"M 61 300 L 70 290 \"/>";
        const string blue = "<path fill=\"none\" stroke-width=\"1.25\" stroke=\"rgb(25.098039%, 58.823529%, 93.333333%)\" stroke-opacity=\"1\" d=\"M 61 310 L 70 280 \"/>";
        const string green = "<path fill=\"none\" stroke-width=\"1.25\" stroke=\"rgb(0%, 54.901961%, 0%)\" stroke-opacity=\"1\" d=\"M 61 320 L 70 270 \"/>";

        var lines = GraphSvgStyle.Apply(Svg(red, blue, green), Palette).Split('\n');

        Assert.Contains($"stroke=\"{Palette.Accent}\"", lines.Single(l => l.Contains("M 61 300")));
        Assert.Contains($"stroke=\"{Palette.Teal}\"", lines.Single(l => l.Contains("M 61 310")));
        Assert.Contains($"stroke=\"{Palette.Ok}\"", lines.Single(l => l.Contains("M 61 320")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an svg")]
    [InlineData("{\"status\":\"error\"}")]
    public void Anything_else_is_left_alone(string input) => Assert.Equal(input, GraphSvgStyle.Apply(input, Palette));
}

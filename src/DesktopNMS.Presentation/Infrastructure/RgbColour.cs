using System.Globalization;

namespace DesktopNMS.Infrastructure;

/// <summary>
/// A colour as red, green and blue - the accent colour's sliders and hex box
/// (#249), without a UI framework's colour type. Views turn
/// <see cref="ToHex"/> into a brush.
/// </summary>
public readonly record struct RgbColour(byte R, byte G, byte B)
{
    /// <summary>DodgerBlue - the accent when none is set or it can't be read.</summary>
    public static RgbColour DefaultAccent { get; } = new(0x1E, 0x90, 0xFF);

    /// <summary>"#RRGGBB", upper case.</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>
    /// Reads "#RGB", "#RRGGBB" or "#AARRGGBB" (alpha ignored), with or
    /// without the "#". False for anything else, including a value still
    /// being typed, so a text box doesn't fight the person typing.
    /// </summary>
    public static bool TryParse(string? text, out RgbColour colour)
    {
        colour = default;
        var hex = text?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(hex) || !hex.All(Uri.IsHexDigit))
        {
            return false;
        }

        switch (hex.Length)
        {
            case 3:
                colour = new RgbColour(Twice(hex[0]), Twice(hex[1]), Twice(hex[2]));
                return true;
            case 6:
                colour = new RgbColour(Byte(hex, 0), Byte(hex, 2), Byte(hex, 4));
                return true;
            case 8:
                colour = new RgbColour(Byte(hex, 2), Byte(hex, 4), Byte(hex, 6));
                return true;
            default:
                return false;
        }

        static byte Byte(string hex, int at) => byte.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

        static byte Twice(char digit) => byte.Parse(new string(digit, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

namespace DesktopNMS.Core.Topology;

/// <summary>
/// Turns a light map tile into a dark one for the dark theme - OpenStreetMap
/// (like most tile servers) only publishes light tiles. Each pixel's
/// lightness is inverted while its hue and saturation are kept, so white
/// land goes near-black and dark labels go light, but water stays blue and
/// parks stay green; then it's toned down slightly and blended toward the
/// app's own background so the map sits in the theme rather than glaring.
/// The same idea as the "invert + hue-rotate 180°" filter dark-mode web
/// maps use, done once per tile as it loads.
/// </summary>
public static class TileRecolour
{
    /// <summary>How much of the original colour survives - a little less than full keeps inverted colours from looking garish.</summary>
    private const double Saturation = 0.75;

    /// <summary>How far each pixel is pulled toward the background colour.</summary>
    private const double BackgroundBlend = 0.2;

    /// <summary>Recolours 32-bit BGRA pixels in place.</summary>
    public static void ToDark(byte[] bgra, byte backgroundR, byte backgroundG, byte backgroundB)
    {
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];

            // Invert HSL lightness, keeping hue and saturation: shifting
            // every channel by (255 - max - min) maps L to 1 - L exactly.
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var shift = 255 - max - min;
            double rr = r + shift, gg = g + shift, bb = b + shift;

            var grey = (rr + gg + bb) / 3;
            rr = grey + (rr - grey) * Saturation;
            gg = grey + (gg - grey) * Saturation;
            bb = grey + (bb - grey) * Saturation;

            bgra[i] = Clamp(bb + (backgroundB - bb) * BackgroundBlend);
            bgra[i + 1] = Clamp(gg + (backgroundG - gg) * BackgroundBlend);
            bgra[i + 2] = Clamp(rr + (backgroundR - rr) * BackgroundBlend);
        }
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}

namespace DesktopNMS.Core.Intelligence;

public enum DashyOutfit { Normal, Bowtie, Fedora, Wizard }

/// <summary>One roll per conversation, never per message or view redraw.</summary>
public static class DashyOutfits
{
    public static DashyOutfit Pick(int roll) => roll switch
    {
        >= 0 and < 50 => DashyOutfit.Normal,
        >= 50 and < 70 => DashyOutfit.Bowtie,
        >= 70 and < 90 => DashyOutfit.Fedora,
        >= 90 and < 100 => DashyOutfit.Wizard,
        _ => throw new ArgumentOutOfRangeException(nameof(roll)),
    };
}

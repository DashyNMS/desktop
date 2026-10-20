namespace DesktopNMS.Services;

/// <summary>Adds or removes the per-user "start at sign-in" registration.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

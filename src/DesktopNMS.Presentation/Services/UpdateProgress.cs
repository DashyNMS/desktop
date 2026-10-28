namespace DesktopNMS.Services;

/// <summary>Real update stages; stages without a measured fraction use an indeterminate bar.</summary>
public enum UpdateStage { Checking, Downloading, Preparing, StartingInstaller }

public sealed record UpdateProgress(UpdateStage Stage)
{
    public string Description => Stage switch
    {
        UpdateStage.Checking => "Checking for the update…",
        UpdateStage.Downloading => "Downloading the update…",
        UpdateStage.Preparing => "Verifying and preparing the update…",
        UpdateStage.StartingInstaller => "Starting the installer…",
        _ => "Preparing the update…",
    };
}

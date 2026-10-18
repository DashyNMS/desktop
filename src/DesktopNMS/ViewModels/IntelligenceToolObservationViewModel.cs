using DesktopNMS.Core.Intelligence;

namespace DesktopNMS.ViewModels;

public sealed class IntelligenceToolObservationViewModel(IntelligenceToolObservation data)
{
    public IntelligenceToolObservation Data { get; } = data;
    public string Title => (string.IsNullOrWhiteSpace(Data.Tool) ? "Lookup" : char.ToUpperInvariant(Data.Tool[0]) + Data.Tool[1..].Replace('_', ' '))
        + " · observed at " + Data.ObservedAt.ToLocalTime().ToString("HH:mm");
    public string Summary => Data.Summary;
    public bool IsError => Data.IsError;
}

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DesktopNMS.Diagnostics;
using DesktopNMS.Infrastructure;
using Xunit;

namespace DesktopNMS.Avalonia.Tests;

public sealed class StartupTimingObserverTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void First_window_readiness_obeys_consent_and_is_not_repeated_after_hiding(bool consent)
    {
        var window = new Window();
        var sink = new Sink();
        StartupTimingObserver.Observe(window, new StartupTiming(sample: () => true), sink, () => consent);
        try
        {
            Assert.Empty(sink.Points);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Hide();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(consent ? 1 : 0, sink.Points.Count);
            if (consent) Assert.Equal(MetricMode.Interactive, sink.Points[0].Mode);
        }
        finally { window.Close(); }
    }
    private sealed class Sink : IAppMetrics
    {
        public List<MetricObservation> Points { get; } = [];
        public void Record(MetricObservation observation) => Points.Add(observation);
    }
}

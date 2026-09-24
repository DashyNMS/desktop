using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Devices;
using DesktopNMS.Infrastructure;
using DesktopNMS.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DesktopNMS.ViewModels;

/// <summary>Where a device in a bulk add has got to.</summary>
public enum BulkDeviceStatus
{
    /// <summary>Can't be added as listed - see its message.</summary>
    Invalid,

    /// <summary>Already in LibreNMS - skipped.</summary>
    Exists,

    Ready,
    Adding,
    Added,
    Failed,
}

/// <summary>
/// Backs "Bulk add devices" (see <see cref="Views.BulkAddDevicesWindow"/>):
/// hostnames pasted one per line, or a CSV with a row per device, each added
/// through the same POST /devices call as Add device with the shared
/// <see cref="Options"/> - anything a CSV row sets overrides them for that
/// device (see <see cref="BulkDeviceImport.BuildRequest"/>).
/// </summary>
/// <remarks>
/// LibreNMS tests each device over SNMP before adding it, which can take
/// several seconds per device, so a few go at once (<see cref="MaxConcurrentAdds"/>)
/// - enough to get through a list quickly without looking like a burst to
/// the server. Rows LibreNMS would reject anyway (a bad hostname, the same
/// device twice, one it already has) are shown up front and never sent.
/// </remarks>
public sealed class BulkAddDevicesViewModel : ObservableObject
{
    private const int MaxConcurrentAdds = 4;
    private const long MaxCsvBytes = 5 * 1024 * 1024;

    private readonly ILibreNmsClient _client;
    private readonly IWindowService _windows;
    private readonly ILogger<BulkAddDevicesViewModel> _logger;

    /// <summary>Hostnames and IPs LibreNMS already has - such rows are skipped rather than sent to fail as duplicates.</summary>
    private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _runCts;
    private BulkDeviceParseResult _csv = BulkDeviceParseResult.Empty;
    private bool _isCsvMode;
    private string _pasteText = string.Empty;
    private string? _csvFileName;
    private bool _isRunning;
    private int _addedCount;

    public BulkAddDevicesViewModel(ILibreNmsClient client, IWindowService windows, ILogger<BulkAddDevicesViewModel> logger)
    {
        _client = client;
        _windows = windows;
        _logger = logger;

        Options = new DeviceAddOptionsViewModel(client, logger, isBulk: true);
        Items = new ObservableCollection<BulkDeviceItemViewModel>();

        AddCommand = new AsyncRelayCommand(() => RunAsync(Items.Where(i => i.Status == BulkDeviceStatus.Ready).ToList()), () => !IsRunning && ReadyCount > 0);
        RetryFailedCommand = new AsyncRelayCommand(RetryFailedAsync, () => !IsRunning && FailedCount > 0);
        StopCommand = new RelayCommand(() => _runCts?.Cancel(), () => IsRunning);
        ImportCsvCommand = new RelayCommand(ImportCsv, () => !IsRunning);
        SaveTemplateCommand = new RelayCommand(SaveTemplate);
        ClearCsvCommand = new RelayCommand(ClearCsv, () => !IsRunning);
        CopyResultsCommand = new RelayCommand(CopyResults, () => Items.Count > 0);

        _ = LoadExistingDevicesAsync();
    }

    /// <summary>The settings every device gets, unless its CSV row says otherwise.</summary>
    public DeviceAddOptionsViewModel Options { get; }

    public ObservableCollection<BulkDeviceItemViewModel> Items { get; }

    public AsyncRelayCommand AddCommand { get; }

    public AsyncRelayCommand RetryFailedCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ImportCsvCommand { get; }

    public RelayCommand SaveTemplateCommand { get; }

    public RelayCommand ClearCsvCommand { get; }

    public RelayCommand CopyResultsCommand { get; }

    /// <summary>Paste a list (false) or import a CSV (true) - a toggle, so switching back and forth keeps what was in each.</summary>
    public bool IsCsvMode
    {
        get => _isCsvMode;
        set
        {
            if (!IsRunning && SetProperty(ref _isCsvMode, value))
            {
                OnPropertyChanged(nameof(IsPasteMode));
                Rebuild();
            }
        }
    }

    public bool IsPasteMode
    {
        get => !_isCsvMode;
        set => IsCsvMode = !value;
    }

    /// <summary>Hostnames or IPs, one per line (bound with a short delay).</summary>
    public string PasteText
    {
        get => _pasteText;
        set
        {
            if (SetProperty(ref _pasteText, value ?? string.Empty) && !IsCsvMode)
            {
                Rebuild();
            }
        }
    }

    public string? CsvFileName
    {
        get => _csvFileName;
        private set
        {
            if (SetProperty(ref _csvFileName, value))
            {
                OnPropertyChanged(nameof(HasCsv));
            }
        }
    }

    public bool HasCsv => _csvFileName is not null;

    /// <summary>What there is to say about the CSV as a whole - a missing hostname column, ignored columns.</summary>
    public string CsvMessage => string.Join(" ", _csv.Messages);

    public bool HasCsvMessage => IsCsvMode && _csv.Messages.Count > 0;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(IsNotRunning));
            }
        }
    }

    public bool IsNotRunning => !IsRunning;

    public int ReadyCount => Items.Count(i => i.Status == BulkDeviceStatus.Ready);

    public int FailedCount => Items.Count(i => i.Status == BulkDeviceStatus.Failed);

    public string AddButtonText => ReadyCount == 1 ? "Add 1 device" : $"Add {ReadyCount} devices";

    public bool HasItems => Items.Count > 0;

    /// <summary>"12 to add, 2 can't be added, 1 already in LibreNMS" - or, once running, how far it's got.</summary>
    public string SummaryText
    {
        get
        {
            if (Items.Count == 0)
            {
                return IsCsvMode ? "Import a CSV to see its devices here." : "Paste or type hostnames or IPs to see them here.";
            }

            var parts = new List<string>();
            void Part(int count, string text)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {text}");
                }
            }

            Part(Items.Count(i => i.Status == BulkDeviceStatus.Added), "added");
            Part(Items.Count(i => i.Status == BulkDeviceStatus.Adding), "adding");
            Part(ReadyCount, IsRunning ? "waiting" : "to add");
            Part(FailedCount, "failed");
            Part(Items.Count(i => i.Status == BulkDeviceStatus.Exists), "already in LibreNMS");
            Part(Items.Count(i => i.Status == BulkDeviceStatus.Invalid), "can't be added");

            return string.Join(", ", parts);
        }
    }

    /// <summary>Closing mid-run stops it first; the dialog reports whether anything was added either way.</summary>
    public void OnClosing()
    {
        _runCts?.Cancel();
    }

    public bool AnyAdded => _addedCount > 0;

    private async Task LoadExistingDevicesAsync()
    {
        try
        {
            var devices = await _client.Devices.ListAsync().ConfigureAwait(true);
            foreach (var device in devices)
            {
                AddExisting(device.Hostname);
                AddExisting(device.Ip);
            }

            // Re-check what's already listed, in case this finished after
            // the list was pasted.
            if (!IsRunning)
            {
                foreach (var item in Items)
                {
                    ApplyExisting(item);
                }

                RaiseCounts();
            }
        }
        catch (LibreNmsApiException ex)
        {
            // Not fatal: LibreNMS still refuses a duplicate itself, it just
            // shows up as a failure rather than being skipped up front.
            _logger.LogWarning(ex, "Could not load the device list to check bulk add for duplicates");
        }
    }

    private void AddExisting(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            _existing.Add(address.Trim());
        }
    }

    /// <summary>Re-reads the paste box or the imported CSV into <see cref="Items"/>.</summary>
    private void Rebuild()
    {
        var result = IsCsvMode ? _csv : BulkDeviceImport.ParseList(PasteText);

        Items.Clear();
        foreach (var row in result.Rows)
        {
            var item = new BulkDeviceItemViewModel(row);
            ApplyExisting(item);
            Items.Add(item);
        }

        OnPropertyChanged(nameof(CsvMessage));
        OnPropertyChanged(nameof(HasCsvMessage));
        RaiseCounts();
    }

    private void ApplyExisting(BulkDeviceItemViewModel item)
    {
        if (item.Status is BulkDeviceStatus.Ready or BulkDeviceStatus.Exists)
        {
            if (_existing.Contains(item.Hostname))
            {
                item.SetStatus(BulkDeviceStatus.Exists, "Already in LibreNMS - skipped.");
            }
            else if (item.Status == BulkDeviceStatus.Exists)
            {
                item.SetStatus(BulkDeviceStatus.Ready, null);
            }
        }
    }

    private void ImportCsv()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Import devices from CSV",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var info = new FileInfo(dialog.FileName);
            if (info.Length > MaxCsvBytes)
            {
                _windows.ShowError("File too large", "That file is over 5 MB - far more than a device list needs. Check it's the right file.");
                return;
            }

            // Detects a UTF-8/UTF-16 byte-order mark; plain UTF-8 otherwise.
            using var reader = new StreamReader(dialog.FileName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _csv = BulkDeviceImport.ParseCsv(reader.ReadToEnd());
            CsvFileName = Path.GetFileName(dialog.FileName);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read {Path}", dialog.FileName);
            _windows.ShowError("Couldn't read the file", ex.Message);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _windows.ShowError("Couldn't read the file", ex.Message);
            return;
        }

        IsCsvMode = true;
        Rebuild();
    }

    private void ClearCsv()
    {
        _csv = BulkDeviceParseResult.Empty;
        CsvFileName = null;
        Rebuild();
    }

    private void SaveTemplate()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "dashynms-devices.csv",
            Title = "Save CSV template",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            File.WriteAllText(dialog.FileName, BulkDeviceImport.Template());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _windows.ShowError("Couldn't save the template", ex.Message);
        }
    }

    /// <summary>Every row with how it went, as CSV - handy for keeping a record of what failed and why.</summary>
    private void CopyResults()
    {
        var csv = CsvWriter.ToCsv(
            new[] { "line", "hostname", "status", "message" },
            Items.Select(i => (IReadOnlyList<string>)new[] { i.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), i.Hostname, i.StatusText, i.Message ?? string.Empty }));

        try
        {
            Clipboard.SetText(csv);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another app holding the clipboard open - rare, and worth a retry.
            _windows.ShowError("Couldn't copy", "The clipboard is in use by another app. Try again in a moment. (" + ex.Message + ")");
        }
    }

    private Task RetryFailedAsync()
    {
        var failed = Items.Where(i => i.Status == BulkDeviceStatus.Failed).ToList();
        foreach (var item in failed)
        {
            item.SetStatus(BulkDeviceStatus.Ready, null);
        }

        return RunAsync(failed);
    }

    private async Task RunAsync(IReadOnlyList<BulkDeviceItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Taken once, so every device in this run gets the same settings even
        // if the form is changed mid-run.
        var shared = Options.BuildRequest(string.Empty);

        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;

        using var gate = new SemaphoreSlim(MaxConcurrentAdds);

        try
        {
            await Task.WhenAll(items.Select(item => AddOneAsync(item, shared, gate, cts.Token))).ConfigureAwait(true);
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
            RaiseCounts();
        }
    }

    private async Task AddOneAsync(BulkDeviceItemViewModel item, AddDeviceRequest shared, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Stopped before this one started - it stays ready to add.
            return;
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var request = BulkDeviceImport.BuildRequest(shared, item.Row);
            if (BulkDeviceImport.RequestError(request) is { } problem)
            {
                item.SetStatus(BulkDeviceStatus.Failed, problem);
                return;
            }

            item.SetStatus(BulkDeviceStatus.Adding, null);
            RaiseCounts();

            // Not cancelled once sent: LibreNMS carries on adding a device
            // whether or not anyone waits for the answer, so abandoning the
            // request would only lose track of whether it worked.
            var result = await _client.Devices.AddAsync(request, CancellationToken.None).ConfigureAwait(true);

            item.SetStatus(BulkDeviceStatus.Added, result.Message);
            _addedCount++;
            AddExisting(item.Hostname);
        }
        catch (LibreNmsApiException ex)
        {
            item.SetStatus(BulkDeviceStatus.Failed, ex.ToUserMessage());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Bulk add failed for {Hostname}", item.Hostname);
            item.SetStatus(BulkDeviceStatus.Failed, ex.Message);
        }
        finally
        {
            gate.Release();
            RaiseCounts();
        }
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(AnyAdded));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        AddCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ImportCsvCommand.RaiseCanExecuteChanged();
        ClearCsvCommand.RaiseCanExecuteChanged();
        CopyResultsCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>One device in a bulk add.</summary>
public sealed class BulkDeviceItemViewModel : ObservableObject
{
    private BulkDeviceStatus _status;
    private string? _message;

    public BulkDeviceItemViewModel(BulkDeviceRow row)
    {
        Row = row;
        _status = row.IsValid ? BulkDeviceStatus.Ready : BulkDeviceStatus.Invalid;
        _message = row.Error;
        SettingsText = DescribeSettings(row);
    }

    public BulkDeviceRow Row { get; }

    public int Line => Row.Line;

    public string Hostname => Row.Hostname;

    /// <summary>What the CSV row sets for this device, or "Shared settings" - passwords never shown.</summary>
    public string SettingsText { get; }

    public BulkDeviceStatus Status => _status;

    public string StatusText => _status switch
    {
        BulkDeviceStatus.Invalid => "Can't add",
        BulkDeviceStatus.Exists => "Already added",
        BulkDeviceStatus.Ready => "Ready",
        BulkDeviceStatus.Adding => "Adding...",
        BulkDeviceStatus.Added => "Added",
        BulkDeviceStatus.Failed => "Failed",
        _ => string.Empty,
    };

    public string? Message => _message;

    public void SetStatus(BulkDeviceStatus status, string? message)
    {
        _status = status;
        _message = message;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Message));
    }

    private static string DescribeSettings(BulkDeviceRow row)
    {
        var v = row.Values;
        if (v.Count == 0)
        {
            return "Shared settings";
        }

        var parts = new List<string>();

        if (v.TryGetValue(BulkDeviceImport.SnmpDisable, out var disable) && BulkDeviceImport.ParseBool(disable) == true)
        {
            parts.Add("ping only");
        }
        else if (v.TryGetValue(BulkDeviceImport.SnmpVersion, out var version))
        {
            parts.Add(version.ToLowerInvariant());
        }

        if (v.ContainsKey(BulkDeviceImport.Community) || v.ContainsKey(BulkDeviceImport.AuthName))
        {
            parts.Add("own credentials");
        }

        if (v.TryGetValue(BulkDeviceImport.Location, out var location))
        {
            parts.Add(location);
        }

        if (v.TryGetValue(BulkDeviceImport.PollerGroup, out var group))
        {
            parts.Add("poller group " + group);
        }

        var described = new HashSet<string>
        {
            BulkDeviceImport.SnmpDisable, BulkDeviceImport.SnmpVersion, BulkDeviceImport.Community,
            BulkDeviceImport.AuthName, BulkDeviceImport.AuthPass, BulkDeviceImport.AuthLevel, BulkDeviceImport.AuthAlgo,
            BulkDeviceImport.CryptoPass, BulkDeviceImport.CryptoAlgo, BulkDeviceImport.Location, BulkDeviceImport.PollerGroup,
        };

        var others = v.Keys.Count(k => !described.Contains(k));
        if (others > 0)
        {
            parts.Add(others == 1 ? "1 more setting" : $"{others} more settings");
        }

        if (parts.Count == 0)
        {
            return "Own credentials";
        }

        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}

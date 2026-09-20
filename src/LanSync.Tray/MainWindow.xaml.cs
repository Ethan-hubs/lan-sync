using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LanSync.Core;
using LanSync.Syncthing;

namespace LanSync.Tray;

public partial class MainWindow : Window
{
    private static readonly Dictionary<TrayStatusLight, Brush> StatusBrushes = new()
    {
        [TrayStatusLight.Offline] = Brushes.Red,
        [TrayStatusLight.Direct] = Brushes.Green,
        [TrayStatusLight.Relay] = Brushes.Goldenrod,
        [TrayStatusLight.Paused] = Brushes.Gray,
        [TrayStatusLight.AuthError] = Brushes.DarkOrange,
    };

    private readonly SyncthingAdapter? _adapter;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<string> _folders = [];
    private readonly ObservableCollection<FolderError> _conflicts = [];
    private readonly ObservableCollection<RestoreFolderItem> _restoreFolders = [];
    private readonly ObservableCollection<VersionEntry> _restoreVersions = [];
    private readonly Dictionary<DeviceId, ConnectionKind> _connectionKinds = [];
    private readonly CancellationTokenSource _connectionObserverCancellation = new();

    public MainWindow()
    {
        InitializeComponent();

        FolderList.ItemsSource = _folders;
        ConflictList.ItemsSource = _conflicts;
        RestoreFolderCombo.ItemsSource = _restoreFolders;
        RestoreVersionList.ItemsSource = _restoreVersions;

        _adapter = CreateAdapter();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;

        if (_adapter is null)
        {
            ApplyStatus(TrayStatusLight.Offline, "引擎未配置（缺少 API key）");
            return;
        }

        PauseButton.IsEnabled = true;
        ResumeButton.IsEnabled = false;

        _timer.Start();
        _ = ObserveConnectionChangesAsync();
        _ = RefreshAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private async void OnTick(object? sender, EventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_adapter is null)
        {
            return;
        }

        try
        {
            await _adapter.PauseAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is SyncthingApiException or HttpRequestException)
        {
            ApplyStatus(TrayStatusLight.Offline, "引擎不可达");
        }
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (_adapter is null)
        {
            return;
        }

        try
        {
            await _adapter.ResumeAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is SyncthingApiException or HttpRequestException)
        {
            ApplyStatus(TrayStatusLight.Offline, "引擎不可达");
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _connectionObserverCancellation.Cancel();
    }

    private async Task RefreshAsync()
    {
        if (_adapter is null)
        {
            return;
        }

        try
        {
            await RefreshFoldersAsync().ConfigureAwait(true);
            await RefreshConflictsAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is SyncthingApiException or HttpRequestException or InvalidDataException)
        {
            ApplyStatus(TrayStatusLight.Offline, "引擎不可达");
        }
    }

    private async Task RefreshFoldersAsync()
    {
        var folders = await _adapter!.GetFoldersAsync().ConfigureAwait(true);
        var folderObjects = folders.OfType<JsonObject>().ToArray();
        var rows = folderObjects
            .Select(FormatFolder)
            .Where(text => !string.IsNullOrEmpty(text));

        _folders.Clear();
        foreach (var row in rows)
        {
            _folders.Add(row);
        }

        var selectedFolderId = (RestoreFolderCombo.SelectedItem as RestoreFolderItem)?.Id;
        _restoreFolders.Clear();
        foreach (var folder in folderObjects)
        {
            var id = folder["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var label = folder["label"]?.GetValue<string>();
            _restoreFolders.Add(new RestoreFolderItem(id, string.IsNullOrWhiteSpace(label) ? id : label));
        }

        RestoreFolderCombo.SelectedItem = _restoreFolders.FirstOrDefault(folder => folder.Id == selectedFolderId) ??
            _restoreFolders.FirstOrDefault();
    }

    private async Task RefreshConflictsAsync()
    {
        var errors = await _adapter!.GetFolderErrorsAsync().ConfigureAwait(true);
        _conflicts.Clear();
        foreach (var error in errors)
        {
            _conflicts.Add(error);
        }
    }

    private static string FormatFolder(JsonObject folder)
    {
        var label = folder["label"]?.GetValue<string>();
        var path = folder["path"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(label))
        {
            return path ?? string.Empty;
        }

        return string.IsNullOrWhiteSpace(path) ? label : $"{label} — {path}";
    }

    private async Task ObserveConnectionChangesAsync()
    {
        try
        {
            await foreach (var change in _adapter!.SubscribeConnectionChangesAsync(
                cancellationToken: _connectionObserverCancellation.Token,
                emitInitialSnapshot: true).ConfigureAwait(false))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _connectionKinds[change.DeviceId] = change.NewKind;
                    ApplyConnectionStatus();
                });
            }
        }
        catch (OperationCanceledException) when (_connectionObserverCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is SyncthingApiException or HttpRequestException or InvalidDataException)
        {
            await Dispatcher.InvokeAsync(() => ApplyStatus(TrayStatusLight.Offline, "引擎不可达"));
        }
    }

    private void ApplyConnectionStatus()
    {
        var status = TrayStatusPolicy.Aggregate(_connectionKinds.Values);
        ApplyStatus(status, DescribeStatus(status));
        PauseButton.IsEnabled = status != TrayStatusLight.Paused;
        ResumeButton.IsEnabled = status == TrayStatusLight.Paused;
    }

    private async void OnLoadVersionsClick(object sender, RoutedEventArgs e)
    {
        await LoadVersionsAsync().ConfigureAwait(true);
    }

    private async Task LoadVersionsAsync()
    {
        if (_adapter is null || RestoreFolderCombo.SelectedItem is not RestoreFolderItem folder)
        {
            SetRestoreMessage("请先选择文件夹。", isWarning: true);
            return;
        }

        var relativePath = RestorePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            SetRestoreMessage("请输入要恢复文件的相对路径。", isWarning: true);
            return;
        }

        try
        {
            var versions = await _adapter.GetVersionsAsync(folder.Id, relativePath).ConfigureAwait(true);
            _restoreVersions.Clear();
            if (versions.TryGetValue(relativePath, out var entries))
            {
                foreach (var entry in entries)
                {
                    _restoreVersions.Add(entry);
                }
            }

            RestoreVersionList.SelectedIndex = _restoreVersions.Count > 0 ? 0 : -1;
            RestoreButton.IsEnabled = _restoreVersions.Count > 0;
            SetRestoreMessage(
                _restoreVersions.Count > 0 ? $"找到 {_restoreVersions.Count} 个可恢复版本。" : "没有可恢复版本。",
                isWarning: false);
        }
        catch (Exception exception)
        {
            SetRestoreMessage($"读取版本失败：{exception.Message}", isWarning: true);
        }
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_adapter is null ||
            RestoreFolderCombo.SelectedItem is not RestoreFolderItem folder ||
            RestoreVersionList.SelectedItem is not VersionEntry version)
        {
            SetRestoreMessage("请选择要恢复的版本。", isWarning: true);
            return;
        }

        var confirmation = MessageBox.Show(
            $"确定恢复 {version.Path} 的版本 {version.VersionTime} 吗？",
            "恢复版本",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        RestoreButton.IsEnabled = false;
        try
        {
            var result = await _adapter.RestoreVersionAsync(
                folder.Id,
                version.Path,
                version.VersionTime).ConfigureAwait(true);
            await LoadVersionsAsync().ConfigureAwait(true);
            var presentation = RestorePresentationPolicy.Create(result);
            var deviceDetails = presentation.UnpausedDevices.Count == 0
                ? string.Empty
                : $"\n本事务未暂停设备：{string.Join(", ", presentation.UnpausedDevices)}";
            var message = presentation.Warning is null
                ? presentation.Summary
                : $"{presentation.Summary}\n{presentation.Warning}{deviceDetails}";
            SetRestoreMessage(message, presentation.RequiresWarning);
            if (presentation.RequiresWarning)
            {
                MessageBox.Show(
                    $"{presentation.Warning}{deviceDetails}",
                    "恢复风险提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception exception)
        {
            SetRestoreMessage($"恢复失败：{exception.Message}", isWarning: true);
        }
        finally
        {
            RestoreButton.IsEnabled = RestoreVersionList.SelectedItem is VersionEntry;
        }
    }

    private void SetRestoreMessage(string message, bool isWarning)
    {
        RestoreResultText.Text = message;
        RestoreResultText.Foreground = isWarning ? Brushes.DarkOrange : Brushes.DarkGreen;
    }

    private static string DescribeStatus(TrayStatusLight status) => status switch
    {
        TrayStatusLight.Direct => "直连",
        TrayStatusLight.Relay => "中继",
        TrayStatusLight.Offline => "离线",
        TrayStatusLight.Paused => "已暂停",
        _ => "未知",
    };

    private void ApplyStatus(TrayStatusLight status, string text)
    {
        StatusDot.Fill = StatusBrushes[status];
        StatusText.Text = text;
    }

    private static SyncthingAdapter? CreateAdapter()
    {
        var apiKey = EngineSettings.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(EngineSettings.BaseAddress, UriKind.Absolute),
        };
        var restClient = new SyncthingRestClient(httpClient, apiKey);
        return new SyncthingAdapter(restClient);
    }

    private sealed record RestoreFolderItem(string Id, string DisplayName);
}

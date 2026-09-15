using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LanSync.Core;
using LanSync.Syncthing;

namespace LanSync.Tray;

public partial class MainWindow : Window
{
    private static readonly Dictionary<StatusLight, Brush> StatusBrushes = new()
    {
        [StatusLight.Offline] = Brushes.Red,
        [StatusLight.Direct] = Brushes.Green,
        [StatusLight.Relay] = Brushes.Goldenrod,
        [StatusLight.Paused] = Brushes.Gray,
        [StatusLight.AuthError] = Brushes.DarkOrange,
    };

    private readonly SyncthingAdapter? _adapter;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<string> _folders = [];
    private readonly ObservableCollection<FolderError> _conflicts = [];

    public MainWindow()
    {
        InitializeComponent();

        FolderList.ItemsSource = _folders;
        ConflictList.ItemsSource = _conflicts;

        _adapter = CreateAdapter();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;

        if (_adapter is null)
        {
            ApplyStatus(StatusLight.Offline, "引擎未配置（缺少 API key）");
            return;
        }

        _timer.Start();
        _ = RefreshAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    private async void OnTick(object? sender, EventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async Task RefreshAsync()
    {
        if (_adapter is null)
        {
            return;
        }

        try
        {
            var connections = await _adapter.GetConnectionsAsync().ConfigureAwait(true);
            var status = StatusLightMapper.Aggregate(connections.Values.ToArray());
            ApplyStatus(status, DescribeStatus(status));

            await RefreshFoldersAsync().ConfigureAwait(true);
            await RefreshConflictsAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is SyncthingApiException or HttpRequestException or InvalidDataException)
        {
            ApplyStatus(StatusLight.Offline, "引擎不可达");
        }
    }

    private async Task RefreshFoldersAsync()
    {
        var folders = await _adapter!.GetFoldersAsync().ConfigureAwait(true);
        var rows = folders
            .OfType<JsonObject>()
            .Select(FormatFolder)
            .Where(text => !string.IsNullOrEmpty(text));

        _folders.Clear();
        foreach (var row in rows)
        {
            _folders.Add(row);
        }
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

    private static string DescribeStatus(StatusLight status) => status switch
    {
        StatusLight.Direct => "直连",
        StatusLight.Relay => "中继",
        StatusLight.Offline => "离线",
        StatusLight.Paused => "已暂停",
        _ => "未知",
    };

    private void ApplyStatus(StatusLight status, string text)
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
}
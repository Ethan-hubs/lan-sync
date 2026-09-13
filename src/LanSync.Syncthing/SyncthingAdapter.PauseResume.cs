using LanSync.Core;

namespace LanSync.Syncthing;

public sealed partial class SyncthingAdapter
{
    public async Task PauseAsync(CancellationToken cancellationToken = default) =>
        await RestClient.PostAsync("/rest/system/pause", cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task PauseAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        await RestClient.PostAsync($"/rest/system/pause?device={Escape(deviceId.Value)}", cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task ResumeAsync(CancellationToken cancellationToken = default) =>
        await RestClient.PostAsync("/rest/system/resume", cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task ResumeAsync(DeviceId deviceId, CancellationToken cancellationToken = default) =>
        await RestClient.PostAsync($"/rest/system/resume?device={Escape(deviceId.Value)}", cancellationToken: cancellationToken).ConfigureAwait(false);
}


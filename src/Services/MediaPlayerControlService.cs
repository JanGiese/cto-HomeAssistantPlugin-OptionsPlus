namespace Loupedeck.HomeAssistantPlugin.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;

    internal sealed class MediaPlayerControlService : IDisposable
    {
        private readonly IHaClient _ha;
        private readonly Func<String, MediaPlayerData?> _getState;
        private readonly ConcurrentDictionary<String, MediaPlayerData> _snapshots = new(StringComparer.Ordinal);
        private readonly Int32 _volumeDebounceMs;
        private readonly Object _gate = new();
        private readonly Dictionary<String, PendingVolume> _pending = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = new();
        private Boolean _disposed;

        public MediaPlayerControlService(IHaClient ha, Func<String, MediaPlayerData?>? getState = null,
            Int32 volumeDebounceMs = 120)
        {
            this._ha = ha ?? throw new ArgumentNullException(nameof(ha));
            this._getState = getState ?? (id => this._snapshots.TryGetValue(id, out var state) ? state : null);
            this._volumeDebounceMs = volumeDebounceMs >= 0 ? volumeDebounceMs
                : throw new ArgumentOutOfRangeException(nameof(volumeDebounceMs));
        }

        public Task<(Boolean ok, String? error)> ExecuteAsync(MediaPlayerData data, MediaPlayerSettings settings,
            String controlId, String? source = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (data.EntityId != settings.EntityId)
            {
                return Task.FromResult<(Boolean ok, String? error)>((false, "Media player entity does not match settings."));
            }

            this._snapshots[data.EntityId] = data;
            return this.ExecuteAsync(settings, controlId, source, ct);
        }

        public Task<(Boolean ok, String? error)> AdjustVolumeAsync(MediaPlayerData data, MediaPlayerSettings settings,
            Int32 diff, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (data.EntityId != settings.EntityId)
            {
                return Task.FromResult<(Boolean ok, String? error)>((false, "Media player entity does not match settings."));
            }

            this._snapshots[data.EntityId] = data;
            return this.AdjustVolumeAsync(settings, diff, ct);
        }

        public Task<(Boolean ok, String? error)> SetVolumeAsync(MediaPlayerData data, MediaPlayerSettings settings,
            Double value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (data.EntityId != settings.EntityId)
            {
                return Task.FromResult<(Boolean ok, String? error)>((false, "Media player entity does not match settings."));
            }

            this._snapshots[data.EntityId] = data;
            return this.SetVolumeAsync(settings, value, ct);
        }

        public async Task<(Boolean ok, String? error)> ExecuteAsync(MediaPlayerSettings settings,
            String controlId, String? source = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var error = this.Validate(settings, controlId, out var state);
            if (error != null)
            {
                return (false, error);
            }

            if (controlId is "volume_down" or "volume_up")
            {
                return state!.Capabilities.VolumeSet && state.VolumeLevel.HasValue
                    ? await this.QueueVolume(settings, controlId,
                        controlId == "volume_up" ? 0.02 : -0.02, true, ct).ConfigureAwait(false)
                    : await this.SendAsync(settings.EntityId, controlId, null, ct).ConfigureAwait(false);
            }

            String service;
            JsonElement? data = null;
            switch (controlId)
            {
                case "play_pause":
                    service = state!.IsPlaying ? "media_pause" : "media_play";
                    break;
                case "previous":
                    service = "media_previous_track";
                    break;
                case "next":
                    service = "media_next_track";
                    break;
                case "stop":
                    service = "media_stop";
                    break;
                case "turn_on":
                    service = "turn_on";
                    break;
                case "turn_off":
                    service = "turn_off";
                    break;
                case "mute":
                    service = "volume_mute";
                    data = JsonSerializer.SerializeToElement(new { is_volume_muted = !state!.IsVolumeMuted!.Value });
                    break;
                case "shuffle":
                    service = "shuffle_set";
                    data = JsonSerializer.SerializeToElement(new { shuffle = !state!.Shuffle!.Value });
                    break;
                case "repeat":
                    service = "repeat_set";
                    data = JsonSerializer.SerializeToElement(new
                    {
                        repeat = state!.Repeat switch { "off" => "all", "all" => "one", _ => "off" },
                    });
                    break;
                case "source":
                    if (source == null || !state!.SourceList.Contains(source, StringComparer.Ordinal))
                    {
                        return (false, "Source is not available.");
                    }

                    service = "select_source";
                    data = JsonSerializer.SerializeToElement(new { source });
                    break;
                default:
                    return (false, "Unknown media player control.");
            }

            return await this.SendAsync(settings.EntityId, service, data, ct).ConfigureAwait(false);
        }

        public async Task<(Boolean ok, String? error)> AdjustVolumeAsync(MediaPlayerSettings settings,
            Int32 ticks, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ticks == 0)
            {
                return (false, "No volume adjustment requested.");
            }

            var control = ticks > 0 ? "volume_up" : "volume_down";
            var error = this.Validate(settings, control, out var state);
            if (error != null)
            {
                return (false, error);
            }

            if (!state!.Capabilities.VolumeSet || !state.VolumeLevel.HasValue)
            {
                for (Int64 i = 0; i < Math.Abs((Int64)ticks); i++)
                {
                    var result = await this.ExecuteAsync(settings, control, ct: ct).ConfigureAwait(false);
                    if (!result.ok)
                    {
                        return result;
                    }
                }

                return (true, null);
            }

            return await this.QueueVolume(settings, control, ticks * 0.02, true, ct).ConfigureAwait(false);
        }

        public Task<(Boolean ok, String? error)> SetVolumeAsync(MediaPlayerSettings settings,
            Double value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var state = this._getState(settings.EntityId);
            var control = state?.VolumeLevel is Double current && value < current ? "volume_down" : "volume_up";
            return this.QueueVolume(settings, control, value, false, ct);
        }

        private String? Validate(MediaPlayerSettings settings, String control, out MediaPlayerData? state,
            Boolean absoluteVolume = false)
        {
            state = this._getState(settings.EntityId);
            return state switch
            {
                _ when this._disposed => "Media player control service is disposed.",
                null => "Media player is unavailable.",
                MediaPlayerData current when current.EntityId != settings.EntityId || !current.IsAvailable
                    => "Media player is unavailable.",
                _ when !settings.IsEnabled(control) => "Control is hidden.",
                MediaPlayerData current when !(absoluteVolume && current.Capabilities.VolumeSet)
                    && !MediaPlayerControls.IsSupported(control, current)
                    => "Control is not supported in the current state.",
                _ => null,
            };
        }

        private Task<(Boolean ok, String? error)> QueueVolume(MediaPlayerSettings settings, String control,
            Double value, Boolean relative, CancellationToken ct)
        {
            var error = this.Validate(settings, control, out var state, absoluteVolume: !relative);
            if (error != null)
            {
                return Task.FromResult<(Boolean ok, String? error)>((false, error));
            }

            if (!Double.IsFinite(value) || !state!.Capabilities.VolumeSet
                || (relative && !state.VolumeLevel.HasValue))
            {
                return Task.FromResult<(Boolean ok, String? error)>((false, "Absolute volume control is unavailable."));
            }

            lock (this._gate)
            {
                if (this._disposed)
                {
                    return Task.FromResult<(Boolean ok, String? error)>((false, "Media player control service is disposed."));
                }

                if (!this._pending.TryGetValue(settings.EntityId, out var pending))
                {
                    pending = new PendingVolume(state.VolumeLevel ?? 0);
                    this._pending.Add(settings.EntityId, pending);
                }

                var request = new VolumeRequest(settings, control, value, relative, ct);
                pending.Requests.Add(request);
                pending.Delay?.Cancel();
                pending.Delay?.Dispose();
                pending.Delay = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token);
                _ = this.FlushVolumeAsync(settings.EntityId, pending, pending.Delay.Token);
                return request.Completion.Task.WaitAsync(ct);
            }
        }

        // Unlike DebouncedSender, callers await the actual result and canceled requests do not alter the target.
        private async Task FlushVolumeAsync(String entityId, PendingVolume pending, CancellationToken delayToken)
        {
            try
            {
                await Task.Delay(this._volumeDebounceMs, delayToken).ConfigureAwait(false);
                lock (this._gate)
                {
                    if (delayToken.IsCancellationRequested || !this._pending.Remove(entityId))
                    {
                        return;
                    }

                    pending.Delay?.Dispose();
                    pending.Delay = null;
                }

                var target = pending.BaseVolume;
                var active = new List<VolumeRequest>();
                foreach (var request in pending.Requests)
                {
                    if (request.Token.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Token);
                        continue;
                    }

                    var error = this.Validate(request.Settings, request.Control, out var state,
                        absoluteVolume: !request.Relative);
                    if (error != null || !state!.Capabilities.VolumeSet)
                    {
                        request.Completion.TrySetResult((false, error ?? "Absolute volume control is unavailable."));
                        continue;
                    }

                    target = Math.Clamp(request.Relative ? target + request.Value : request.Value, 0, 1);
                    active.Add(request);
                }

                if (active.Count > 0)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                        this._lifetime.Token, active[^1].Token);
                    var result = await this.SendAsync(entityId, "volume_set",
                        JsonSerializer.SerializeToElement(new { volume_level = target }), cts.Token).ConfigureAwait(false);
                    foreach (var request in active)
                    {
                        request.Completion.TrySetResult(result);
                    }
                }
            }
            catch (OperationCanceledException) when (delayToken.IsCancellationRequested && !this._lifetime.IsCancellationRequested)
            {
                // A newer target owns these requests, or CancelPending completed them.
            }
            catch (OperationCanceledException)
            {
                foreach (var request in pending.Requests)
                {
                    request.Completion.TrySetCanceled();
                }
            }
            catch (Exception ex)
            {
                foreach (var request in pending.Requests)
                {
                    request.Completion.TrySetResult((false, ex.Message));
                }
            }
        }

        private async Task<(Boolean ok, String? error)> SendAsync(String entityId, String service,
            JsonElement? data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!this._ha.IsAuthenticated)
            {
                return (false, "Connection lost.");
            }

            try
            {
                return await this._ha.CallServiceAsync("media_player", service, entityId, data, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        public void CancelPending(String entityId)
        {
            lock (this._gate)
            {
                if (this._pending.Remove(entityId, out var pending))
                {
                    pending.Delay?.Cancel();
                    pending.Delay?.Dispose();
                    foreach (var request in pending.Requests)
                    {
                        request.Completion.TrySetCanceled();
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (this._gate)
            {
                if (this._disposed)
                {
                    return;
                }

                this._disposed = true;
                this._lifetime.Cancel();
                foreach (var entityId in this._pending.Keys.ToArray())
                {
                    this.CancelPending(entityId);
                }
            }
        }

        private sealed class PendingVolume(Double baseVolume)
        {
            public Double BaseVolume { get; } = baseVolume;
            public List<VolumeRequest> Requests { get; } = new();
            public CancellationTokenSource? Delay { get; set; }
        }

        private sealed record VolumeRequest(MediaPlayerSettings Settings, String Control, Double Value,
            Boolean Relative, CancellationToken Token)
        {
            public TaskCompletionSource<(Boolean ok, String? error)> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
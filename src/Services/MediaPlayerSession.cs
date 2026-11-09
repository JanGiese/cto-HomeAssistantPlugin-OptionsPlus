namespace Loupedeck.HomeAssistantPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;

    internal sealed class MediaPlayerSession : IDisposable
    {
        private readonly Func<(String Url, String Token)?> _configuration;
        private readonly HaEventListener _events = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _refreshGate = new(1);
        private readonly Object _gate = new();
        private readonly Dictionary<String, MediaPlayerData> _players = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<String, Int64> _revisions = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _eventLifetime;
        private Int64 _revision;
        private Task? _monitor;
        private (String Url, String Token)? _connectedConfiguration;
        private Boolean _disposed;
        private Boolean _isConnected;

        internal MediaPlayerSession(IHaClient client, Func<(String Url, String Token)?> configuration)
        {
            this.Client = client;
            this._configuration = configuration;
            this._events.MediaPlayerStateChanged += this.OnStateChanged;
        }

        internal event Action? Changed;

        internal IHaClient Client { get; }
        internal CancellationToken Lifetime => this._lifetime.Token;
        internal Boolean IsConnected
        {
            get => this._isConnected && !this._disposed && this.Client.IsAuthenticated && this._events.IsConnected;
            private set => this._isConnected = value;
        }
        private String _status = "Connecting...";
        internal String Status
        {
            get => this._status == "Connected" && !this.IsConnected ? "Disconnected" : this._status;
            private set => this._status = value;
        }

        internal IReadOnlyList<MediaPlayerData> Players
        {
            get
            {
                lock (this._gate)
                {
                    return this._players.Values.OrderBy(p => p.FriendlyName, StringComparer.CurrentCultureIgnoreCase).ToArray();
                }
            }
        }

        internal MediaPlayerData? Get(String entityId)
        {
            lock (this._gate)
            {
                return this._players.GetValueOrDefault(entityId);
            }
        }

        internal void Start()
        {
            lock (this._gate)
            {
                if (!this._disposed)
                {
                    this._monitor ??= this.MonitorAsync();
                }
            }
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (!this._lifetime.IsCancellationRequested)
                {
                    await this.RefreshAsync(this._lifetime.Token).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(15), this._lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (this._lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                await this._events.SafeCloseAsync().ConfigureAwait(false);
                await this.Client.SafeCloseAsync().ConfigureAwait(false);
                this._eventLifetime?.Dispose();
                this._events.Dispose();
            }
        }

        internal async Task<Boolean> RefreshAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var entered = false;
            try
            {
                await this._refreshGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                var configuration = this._configuration();
                if (!configuration.HasValue)
                {
                    this.IsConnected = false;
                    this.Status = "Configure Home Assistant";
                    await this._events.SafeCloseAsync().ConfigureAwait(false);
                    await this.Client.SafeCloseAsync().ConfigureAwait(false);
                    return false;
                }

                if (configuration != this._connectedConfiguration || !this.Client.IsAuthenticated)
                {
                    this.IsConnected = false;
                    if (configuration != this._connectedConfiguration)
                    {
                        lock (this._gate)
                        {
                            this._players.Clear();
                            this._revisions.Clear();
                            this._revision = 0;
                        }
                    }
                    var result = await this.Client.ConnectAndAuthenticateAsync(
                        configuration.Value.Url, configuration.Value.Token, TimeSpan.FromSeconds(8), timeout.Token).ConfigureAwait(false);
                    if (!result.ok)
                    {
                        this.Status = "Connection failed";
                        return false;
                    }
                }

                // This listener belongs only to media widgets; closing a folder must not interrupt other actions.
                if (configuration != this._connectedConfiguration || !this._events.IsConnected)
                {
                    await this._events.SafeCloseAsync().ConfigureAwait(false);
                    this._eventLifetime?.Cancel();
                    this._eventLifetime?.Dispose();
                    this._eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token, timeout.Token);
                    this._eventLifetime.CancelAfter(TimeSpan.FromSeconds(8));
                    if (!await this._events.ConnectAndSubscribeAsync(
                        configuration.Value.Url, configuration.Value.Token, this._eventLifetime.Token).ConfigureAwait(false))
                    {
                        this.IsConnected = false;
                        this.Status = "State updates disconnected";
                        return false;
                    }
                    this._eventLifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                }

                this._connectedConfiguration = configuration;
                Int64 revision;
                lock (this._gate)
                {
                    revision = this._revision;
                }

                var response = await this.Client.RequestAsync("get_states", timeout.Token).ConfigureAwait(false);
                if (!response.ok || String.IsNullOrEmpty(response.resultJson))
                {
                    this.IsConnected = false;
                    this.Status = "Could not load players";
                    return false;
                }

                this.ApplySnapshot(response.resultJson, revision);
                this.IsConnected = true;
                this.Status = "Connected";
                return true;
            }
            catch (OperationCanceledException) when (!entered || cancellationToken.IsCancellationRequested || this._lifetime.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                this.IsConnected = false;
                this.Status = "Connection timed out";
                return false;
            }
            catch (Exception ex)
            {
                this.IsConnected = false;
                this.Status = "Connection failed";
                PluginLog.Warning(ex, "[MediaPlayer] Refresh failed");
                return false;
            }
            finally
            {
                if (entered)
                {
                    this._refreshGate.Release();
                }
                this.NotifyChanged();
            }
        }

        internal void ApplySnapshot(String json, Int64 revision)
        {
            var players = MediaPlayerData.ParseStates(json).ToDictionary(p => p.EntityId, StringComparer.OrdinalIgnoreCase);
            lock (this._gate)
            {
                foreach (var id in this._players.Keys.Union(players.Keys).ToArray())
                {
                    // Events received while get_states was in flight are newer than its snapshot.
                    if (this._revisions.GetValueOrDefault(id) > revision)
                    {
                        continue;
                    }
                    if (players.TryGetValue(id, out var player))
                    {
                        this._players[id] = player;
                    }
                    else
                    {
                        this._players.Remove(id);
                    }
                }
            }
        }

        internal void OnStateChanged(String entityId, JsonElement? state)
        {
            var player = state.HasValue ? MediaPlayerData.Parse(state.Value) : null;
            lock (this._gate)
            {
                this._revisions[entityId] = ++this._revision;
                if (player != null)
                {
                    this._players[entityId] = player;
                }
                else
                {
                    this._players.Remove(entityId);
                }
            }
            this.NotifyChanged();
        }

        private void NotifyChanged()
        {
            foreach (var subscriber in this.Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "[MediaPlayer] UI refresh failed");
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
                this._events.MediaPlayerStateChanged -= this.OnStateChanged;
                this._lifetime.Cancel();
            }
            this.IsConnected = false;
            this.Changed = null;
        }
    }
}
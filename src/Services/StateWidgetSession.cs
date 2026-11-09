namespace Loupedeck.HomeAssistantPlugin.Services
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;

    internal interface IStateWidgetEvents : IDisposable
    {
        event Action<String, JsonElement?>? EntityStateChanged;
        Boolean IsConnected { get; }
        Task<Boolean> ConnectAndSubscribeAsync(String url, String token, CancellationToken cancellationToken);
        Task SafeCloseAsync();
    }

    internal sealed class StateWidgetSession : IDisposable
    {
        private readonly IHaClient _client;
        private readonly IStateWidgetEvents _events;
        private readonly Func<(String Url, String Token)?> _configuration;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _requests = new(1, 1);
        private readonly Object _gate = new();
        private readonly Dictionary<String, StateWidgetEntity> _entities = new(StringComparer.Ordinal);
        private readonly Dictionary<String, Int64> _revisions = new(StringComparer.Ordinal);
        private CancellationTokenSource? _eventLifetime;
        private (String Url, String Token)? _connectedConfiguration;
        private Int64 _revision;
        private Task? _monitor;
        private volatile Boolean _disposed;
        private volatile Boolean _loaded;
        private String _status = "Connecting...";

        internal StateWidgetSession(IHaClient client, Func<(String Url, String Token)?> configuration, IStateWidgetEvents? events = null)
        {
            this._client = client;
            this._configuration = configuration;
            this._events = events ?? new HaEventListener();
            this._events.EntityStateChanged += this.OnStateChanged;
        }

        internal event Action? Changed;

        internal Boolean IsConnected => !this._disposed && this._loaded && this._client.IsAuthenticated &&
            this._events.IsConnected && this._configuration() == this._connectedConfiguration;

        internal String Status => this._status == "Connected" && !this.IsConnected ? "Disconnected" : this._status;

        internal IReadOnlyList<StateWidgetEntity> Entities
        {
            get
            {
                lock (this._gate)
                {
                    return this._entities.Values.OrderBy(e => e.EntityId, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }
        }

        internal StateWidgetEntity? Get(String entityId)
        {
            lock (this._gate)
            {
                return this._entities.GetValueOrDefault(entityId);
            }
        }

        internal void Start()
        {
            lock (this._gate)
            {
                if (!this._disposed)
                {
                    this._monitor ??= Task.Run(this.MonitorAsync);
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
        }

        internal async Task<Boolean> RefreshAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var entered = false;
            try
            {
                await this._requests.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                if (this._disposed)
                {
                    return false;
                }
                var configuration = this._configuration();
                if (!configuration.HasValue || configuration != this._connectedConfiguration ||
                    !this._client.IsAuthenticated || !this._events.IsConnected)
                {
                    this._loaded = false;
                    this.NotifyChanged();
                    this._eventLifetime?.Cancel();
                    await this._events.SafeCloseAsync().ConfigureAwait(false);
                    await this._client.SafeCloseAsync().ConfigureAwait(false);
                    this._eventLifetime?.Dispose();
                    this._eventLifetime = null;
                    if (configuration != this._connectedConfiguration)
                    {
                        lock (this._gate)
                        {
                            this._entities.Clear();
                            this._revisions.Clear();
                            this._revision = 0;
                        }
                    }
                    this._connectedConfiguration = null;
                    if (!configuration.HasValue)
                    {
                        this._status = "Configure Home Assistant";
                        return false;
                    }
                    var result = await this._client.ConnectAndAuthenticateAsync(configuration.Value.Url,
                        configuration.Value.Token, TimeSpan.FromSeconds(8), timeout.Token).ConfigureAwait(false);
                    if (!result.ok)
                    {
                        this._status = "Connection failed";
                        return false;
                    }
                    // This token must outlive the snapshot request and its timeout.
                    this._eventLifetime = CancellationTokenSource.CreateLinkedTokenSource(this._lifetime.Token);
                    this._eventLifetime.CancelAfter(TimeSpan.FromSeconds(8));
                    if (!await this._events.ConnectAndSubscribeAsync(configuration.Value.Url,
                        configuration.Value.Token, this._eventLifetime.Token).ConfigureAwait(false))
                    {
                        this._status = "State updates disconnected";
                        return false;
                    }
                    this._eventLifetime.CancelAfter(Timeout.InfiniteTimeSpan);
                    this._connectedConfiguration = configuration;
                }

                Int64 revision;
                lock (this._gate)
                {
                    revision = this._revision;
                }
                var response = await this._client.RequestAsync("get_states", timeout.Token).ConfigureAwait(false);
                if (!response.ok || String.IsNullOrEmpty(response.resultJson))
                {
                    this._loaded = false;
                    this._status = "Could not load entities";
                    return false;
                }
                this.ApplySnapshot(response.resultJson, revision);
                this._loaded = true;
                this._status = "Connected";
                return this.IsConnected;
            }
            catch (OperationCanceledException)
            {
                this._loaded = false;
                this._status = "Connection timed out";
                return false;
            }
            catch (Exception)
            {
                this._loaded = false;
                this._status = "Connection failed";
                return false;
            }
            finally
            {
                if (entered)
                {
                    this._requests.Release();
                }
                this.NotifyChanged();
            }
        }

        internal void ApplySnapshot(String json, Int64 revision)
        {
            using var document = JsonDocument.Parse(json);
            var entities = document.RootElement.EnumerateArray().Select(StateWidgetEntity.Parse)
                .Where(e => e != null).Select(e => e!).ToDictionary(e => e.EntityId, StringComparer.Ordinal);
            lock (this._gate)
            {
                foreach (var id in this._entities.Keys.Union(entities.Keys).ToArray())
                {
                    if (this._revisions.GetValueOrDefault(id) > revision)
                    {
                        continue;
                    }
                    if (entities.TryGetValue(id, out var entity))
                    {
                        this._entities[id] = entity;
                    }
                    else
                    {
                        this._entities.Remove(id);
                    }
                }
            }
        }

        internal void OnStateChanged(String entityId, JsonElement? state)
        {
            if (this._disposed)
            {
                return;
            }
            var entity = state.HasValue ? StateWidgetEntity.Parse(state.Value) : null;
            lock (this._gate)
            {
                this._revisions[entityId] = ++this._revision;
                if (entity != null && entity.EntityId == entityId)
                {
                    this._entities[entityId] = entity;
                }
                else
                {
                    this._entities.Remove(entityId);
                }
            }
            this.NotifyChanged();
        }

        internal async Task<(Boolean Ok, String? Error)> ExecuteAsync(StateWidgetSettings settings, CancellationToken cancellationToken)
        {
            if (settings.Error != null)
            {
                return (false, settings.Error);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var entered = false;
            try
            {
                await this._requests.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                if (!this.IsConnected || this.Get(settings.EntityId)?.IsAvailable != true)
                {
                    return (false, "State entity is missing, unavailable, or disconnected.");
                }
                var result = await this._client.CallServiceAsync(settings.Domain, settings.Service,
                    settings.TargetEntityId, settings.Data, timeout.Token).ConfigureAwait(false);
                return result.ok ? (true, null) : (false, "Home Assistant rejected the service call.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Service call timed out.");
            }
            catch (Exception)
            {
                return (false, "Service call failed.");
            }
            finally
            {
                if (entered)
                {
                    this._requests.Release();
                }
            }
        }

        private void NotifyChanged()
        {
            foreach (var subscriber in this.Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception)
                {
                    PluginLog.Warning("[StateWidget] Image refresh failed");
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
                this._events.EntityStateChanged -= this.OnStateChanged;
                this.Changed = null;
                this._lifetime.Cancel();
            }
            this.CloseAsync().GetAwaiter().GetResult();
        }

        private async Task CloseAsync()
        {
            await this._requests.WaitAsync().ConfigureAwait(false);
            try
            {
                this._eventLifetime?.Cancel();
                this._events.Dispose();
                await this._client.SafeCloseAsync().ConfigureAwait(false);
                this._eventLifetime?.Dispose();
            }
            finally
            {
                this._requests.Release();
            }
        }
    }
}
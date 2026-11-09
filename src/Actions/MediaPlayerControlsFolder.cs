namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public sealed class MediaPlayerControlsFolder : PluginDynamicFolder
    {
        private MediaPlayerSession? _session;
        private MediaPlayerControlService? _controls;
        private MediaPlayerSettings? _settings;
        private CancellationTokenSource _viewLifetime = new();
        private String _context = Guid.NewGuid().ToString("N");
        private Boolean _active;
        private Boolean _sources;
        private String? _error;

        public MediaPlayerControlsFolder()
        {
            this.DisplayName = "Media Player Controls";
            this.GroupName = "Media Players";
            this.Description = "Use a configured Media Player widget to open these controls.";
        }

        public override PluginDynamicFolderNavigation GetNavigationArea(DeviceType _) => PluginDynamicFolderNavigation.ButtonArea;

        public override BitmapImage GetButtonImage(PluginImageSize imageSize) =>
            new PluginResourceProvider(typeof(HomeAssistantPlugin).Assembly).LoadImage("media_player_icon.svg")!;

        public override Boolean Load()
        {
            if (this.Plugin is not HomeAssistantPlugin plugin)
            {
                return false;
            }
            this._session = plugin.MediaPlayers;
            this.CancelView();
            this._controls = new MediaPlayerControlService(this._session.Client,
                id => this._session.IsConnected ? this._session.Get(id) : null);
            this._session.Changed += this.OnStateChanged;
            plugin.MediaPlayerFolder = this;
            return true;
        }

        internal void Configure(MediaPlayerSettings settings)
        {
            this.CancelView();
            this._settings = settings;
            this._context = Guid.NewGuid().ToString("N");
            this._sources = false;
            this._error = null;
            this.OnStateChanged();
        }

        public override Boolean Activate()
        {
            this._active = true;
            if (this.Plugin is HomeAssistantPlugin plugin && plugin.MediaPlayerSelection != null)
            {
                this.Configure(plugin.MediaPlayerSelection);
            }
            this._session?.Start();
            _ = this.RefreshAsync();
            return true;
        }

        public override Boolean Deactivate()
        {
            this._active = false;
            this.CancelView();
            return true;
        }

        public override Boolean Unload()
        {
            this.Deactivate();
            this._viewLifetime.Dispose();
            if (this._session != null)
            {
                this._session.Changed -= this.OnStateChanged;
            }
            this._controls?.Dispose();
            if (this.Plugin is HomeAssistantPlugin plugin && plugin.MediaPlayerFolder == this)
            {
                plugin.MediaPlayerFolder = null;
            }
            return true;
        }

        private void CancelView()
        {
            this._viewLifetime.Cancel();
            this._viewLifetime.Dispose();
            this._viewLifetime = this._session == null ? new CancellationTokenSource() :
                CancellationTokenSource.CreateLinkedTokenSource(this._session.Lifetime);
            if (this._settings != null)
            {
                this._controls?.CancelPending(this._settings.EntityId);
            }
        }

        private MediaPlayerData? Player => this._settings == null ? null : this._session?.Get(this._settings.EntityId);

        private IEnumerable<String> VisibleControls
        {
            get
            {
                var settings = this._settings;
                var player = settings == null ? null : this._session?.Get(settings.EntityId);
                return settings == null || player == null || this._session?.IsConnected != true
                    ? Enumerable.Empty<String>() : settings.GetVisibleControls(player);
            }
        }

        private String Command(String control) => this.CreateCommandName(this.Encode(control));

        internal String Encode(String control) => this._context + ":" + control;

        internal Boolean TryDecode(String parameter, out String control)
        {
            var prefix = this._context + ":";
            control = parameter.StartsWith(prefix, StringComparison.Ordinal) ? parameter.Substring(prefix.Length) : String.Empty;
            return control.Length > 0;
        }

        public override IEnumerable<String> GetButtonPressActionNames(DeviceType _)
        {
            yield return this.Command("back");
            yield return this.Command("status");
            yield return this.Command("retry");
            var player = this.Player;
            if (this._sources)
            {
                if (player != null && this.VisibleControls.Contains("source"))
                {
                    foreach (var source in player.SourceList)
                    {
                        yield return this.Command("select_source:" + Uri.EscapeDataString(source));
                    }
                }
                yield break;
            }

            var controls = this.VisibleControls.ToArray();
            foreach (var control in controls)
            {
                yield return this.Command(control);
            }
            if (controls.Length == 0 && this._settings != null && player?.IsAvailable == true && this._session?.IsConnected == true)
            {
                yield return this.Command("empty");
            }
        }

        public override IEnumerable<String> GetEncoderRotateActionNames(DeviceType _)
        {
            if (!this._sources && this.VisibleControls.Any(c => c is "volume_down" or "volume_up") &&
                this.Player?.Capabilities.VolumeSet == true)
            {
                yield return this.CreateAdjustmentName(this.Encode("volume"));
            }
        }

        public override String GetCommandDisplayName(String actionParameter, PluginImageSize _)
        {
            var player = this.Player;
            return !this.TryDecode(actionParameter, out var control)
                ? String.Empty
                : control switch
                {
                    "back" => "Back",
                    "status" => player?.FriendlyName ?? "Media Player",
                    "retry" => "Refresh",
                    "empty" => "No enabled supported controls",
                    "play_pause" => player?.State == "playing" ? "Pause" : "Play",
                    "mute" => player?.IsVolumeMuted == true ? "Unmute" : "Mute",
                    "source" => "Source: " + (player?.Source ?? "-"),
                    "shuffle" => player?.Shuffle == true ? "Shuffle: On" : "Shuffle: Off",
                    "repeat" => "Repeat: " + (player?.Repeat ?? "-"),
                    _ when control.StartsWith("select_source:", StringComparison.Ordinal) => Uri.UnescapeDataString(control.Substring("select_source:".Length)),
                    _ => MediaPlayerSettings.Controls.FirstOrDefault(c => c.Id == control)?.Label ?? String.Empty
                };
        }

        public override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var label = this.GetCommandDisplayName(actionParameter, imageSize);
            var player = this.Player;
            var detail = String.Empty;
            if (this.TryDecode(actionParameter, out var control) && control == "status")
            {
                detail = this._error ?? (this._session?.IsConnected != true ? this._session?.Status ?? "Connecting..." :
                    this._settings == null ? "Configure a Media Player widget" :
                    player == null ? "Player not found" :
                    String.Join("\n", new[] { player.State, player.MediaTitle, player.MediaArtist }
                        .Where(s => !String.IsNullOrEmpty(s))));
            }
            using var dimensions = new BitmapBuilder(imageSize);
            return MediaPlayerTile.Draw(dimensions.Width, dimensions.Height, label, detail,
                this._session?.IsConnected == true && player?.IsAvailable == true && this._error == null);
        }

        public override String GetAdjustmentDisplayName(String actionParameter, PluginImageSize _) => "Volume";

        public override String GetAdjustmentValue(String actionParameter) => this.Player?.VolumeLevel is Double level
            ? $"{level * 100:F0}%" : "-";

        public override BitmapImage GetAdjustmentImage(String actionParameter, PluginImageSize imageSize)
        {
            using var dimensions = new BitmapBuilder(imageSize);
            return MediaPlayerTile.Draw(dimensions.Width, dimensions.Height, "Volume", this.GetAdjustmentValue(actionParameter),
                this._session?.IsConnected == true && this.Player?.IsAvailable == true);
        }

        public override void RunCommand(String actionParameter)
        {
            if (!this._active || !this.TryDecode(actionParameter, out var control))
            {
                return;
            }
            if (control == "back")
            {
                if (this._sources)
                {
                    this.CancelView();
                    this._context = Guid.NewGuid().ToString("N");
                    this._sources = false;
                    this.OnStateChanged();
                }
                else
                {
                    this.CancelView();
                    this.Close();
                }
                return;
            }
            if (control == "retry")
            {
                _ = this.RefreshAsync();
                return;
            }
            if (control == "source" && this.VisibleControls.Contains("source"))
            {
                this.CancelView();
                this._context = Guid.NewGuid().ToString("N");
                this._sources = true;
                this.OnStateChanged();
                return;
            }
            if (control is "status" or "empty")
            {
                return;
            }
            _ = this.ExecuteAsync(control, this._context, this._viewLifetime.Token);
        }

        private async Task RefreshAsync()
        {
            if (this._session == null)
            {
                return;
            }
            var context = this._context;
            var ok = await this._session.RefreshAsync(this._viewLifetime.Token).ConfigureAwait(false);
            if (context == this._context && this._active && ok)
            {
                this._error = null;
                this.OnStateChanged();
            }
        }

        private async Task ExecuteAsync(String control, String context, CancellationToken token)
        {
            var settings = this._settings;
            var player = settings == null ? null : this._session?.Get(settings.EntityId);
            if (settings == null || this._controls == null || player == null || this._session?.IsConnected != true ||
                context != this._context || token.IsCancellationRequested)
            {
                return;
            }
            try
            {
                String? source = null;
                if (control.StartsWith("select_source:", StringComparison.Ordinal))
                {
                    source = Uri.UnescapeDataString(control.Substring("select_source:".Length));
                    control = "source";
                }
                var result = await this._controls.ExecuteAsync(player, settings, control, source, token).ConfigureAwait(false);
                if (context != this._context || token.IsCancellationRequested)
                {
                    return;
                }
                this._error = result.ok ? null : result.error ?? "Command failed";
                if (result.ok && control == "source")
                {
                    this._sources = false;
                    this._context = Guid.NewGuid().ToString("N");
                }
                this.OnStateChanged();
                if (result.ok && control is not "volume_up" and not "volume_down")
                {
                    await this._session.RefreshAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (context == this._context && !token.IsCancellationRequested)
                {
                    this._error = "Command failed";
                    this.OnStateChanged();
                }
                PluginLog.Warning(ex, "[MediaPlayer] Command failed");
            }
        }

        public override void ApplyAdjustment(String actionParameter, Int32 diff)
        {
            if (!this._active || !this.TryDecode(actionParameter, out var control) || control != "volume" || diff == 0 ||
                !this.VisibleControls.Contains(diff > 0 ? "volume_up" : "volume_down") ||
                this.Player == null || this._settings == null || this._controls == null)
            {
                return;
            }
            _ = this.AdjustVolumeAsync(diff, this._context, this._viewLifetime.Token);
        }

        private async Task AdjustVolumeAsync(Int32 diff, String context, CancellationToken token)
        {
            var settings = this._settings;
            var player = settings == null ? null : this._session?.Get(settings.EntityId);
            if (settings == null || player == null || context != this._context || token.IsCancellationRequested)
            {
                return;
            }
            try
            {
                var result = await this._controls!.AdjustVolumeAsync(player, settings, diff, token).ConfigureAwait(false);
                if (context == this._context && !token.IsCancellationRequested)
                {
                    this._error = result.ok ? null : result.error ?? "Volume command failed";
                    this.OnStateChanged();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (context == this._context && !token.IsCancellationRequested)
                {
                    this._error = "Volume command failed";
                    this.OnStateChanged();
                }
                PluginLog.Warning(ex, "[MediaPlayer] Volume adjustment failed");
            }
        }

        private void OnStateChanged()
        {
            if (this._active)
            {
                this.ButtonActionNamesChanged();
                this.EncoderActionNamesChanged();
                this.CommandImageChanged(this.Encode("status"));
                foreach (var control in this.VisibleControls)
                {
                    this.CommandImageChanged(this.Encode(control));
                }
                this.AdjustmentValueChanged(this.Encode("volume"));
            }
        }
    }
}
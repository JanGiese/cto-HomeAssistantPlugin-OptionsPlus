namespace Loupedeck.HomeAssistantPlugin
{
    using System;
    using System.Linq;
    using System.Threading;

    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public sealed class MediaPlayerAction : ActionEditorCommand
    {
        internal const String PlayerControl = "ha_media_player";
        private MediaPlayerSession? _session;

        public MediaPlayerAction()
        {
            this.Name = "HomeAssistant.MediaPlayer";
            this.DisplayName = "Media Player";
            this.GroupName = "Media Players";
            this.Description = "Open configurable controls for a Home Assistant media player.";
            this.SetWidget(true);
            this.ActionEditor.AddControlEx(new ActionEditorListbox(PlayerControl, "Media Player"));
            foreach (var control in MediaPlayerSettings.Controls)
            {
                this.ActionEditor.AddControlEx(new ActionEditorCheckbox(control.Id, "Show " + control.Label)
                    .SetDefaultValue(control.DefaultEnabled));
            }
            this.ActionEditor.ListboxItemsRequested += this.OnListboxItemsRequested;
        }

        protected override Boolean OnLoad()
        {
            if (this.Plugin is not HomeAssistantPlugin plugin)
            {
                return false;
            }
            this._session = plugin.MediaPlayers;
            this._session.Changed += this.OnStateChanged;
            this._session.Start();
            return true;
        }

        protected override Boolean OnUnload()
        {
            if (this._session != null)
            {
                this._session.Changed -= this.OnStateChanged;
                this._session = null;
            }
            return true;
        }

        private void OnStateChanged() => this.ActionImageChanged();

        internal static MediaPlayerSettings ReadSettings(ActionEditorActionParameters parameters) =>
            new(parameters.GetString(PlayerControl, String.Empty),
                MediaPlayerSettings.Controls.Where(control => parameters.GetBoolean(control.Id, control.DefaultEnabled))
                    .Select(control => control.Id));

        protected override Boolean RunCommand(ActionEditorActionParameters parameters)
        {
            var settings = ReadSettings(parameters);
            if (this.Plugin is not HomeAssistantPlugin plugin ||
                !settings.EntityId.StartsWith("media_player.", StringComparison.OrdinalIgnoreCase) ||
                settings.EntityId.Length <= "media_player.".Length)
            {
                return false;
            }

            plugin.OpenMediaPlayer(settings);
            return true;
        }

        protected override String GetCommandDisplayName(ActionEditorActionParameters parameters)
        {
            var settings = ReadSettings(parameters);
            return this._session?.Get(settings.EntityId)?.FriendlyName ?? "Media Player";
        }

        protected override BitmapImage GetCommandImage(ActionEditorActionParameters parameters, Int32 width, Int32 height)
        {
            var settings = ReadSettings(parameters);
            var player = this._session?.Get(settings.EntityId);
            var connected = this._session?.IsConnected == true;
            var text = player == null ? "Media Player" : player.FriendlyName;
            var detail = !connected ? this._session?.Status ?? "Connecting..." :
                player == null ? "Player not found" :
                String.IsNullOrEmpty(player.MediaTitle) ? player.State :
                String.Join("\n", new[] { player.State, player.MediaTitle, player.MediaArtist }.Where(s => !String.IsNullOrEmpty(s)));
            return MediaPlayerTile.Draw(width, height, text, detail, connected && player?.IsAvailable == true);
        }

        private void OnListboxItemsRequested(Object? sender, ActionEditorListboxItemsRequestedEventArgs e)
        {
            if (e.ControlName != PlayerControl || this._session == null)
            {
                return;
            }

            this._session.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            var players = this._session.Players;
            foreach (var player in players)
            {
                e.AddItem(player.EntityId, player.FriendlyName, player.EntityId);
            }

            var selected = e.ActionEditorState?.GetControlValue(PlayerControl);
            if (!String.IsNullOrEmpty(selected) && !players.Any(p => p.EntityId == selected))
            {
                e.AddItem(selected, selected, "Currently unavailable");
            }
            if (players.Count == 0 && String.IsNullOrEmpty(selected))
            {
                e.AddItem("!unavailable", this._session.IsConnected ? "No media players found" : this._session.Status, "Retry by reopening this list");
            }
            if (!String.IsNullOrEmpty(selected))
            {
                e.SetSelectedItemName(selected);
            }
        }
    }

    internal static class MediaPlayerTile
    {
        internal static BitmapImage Draw(Int32 width, Int32 height, String title, String detail, Boolean available)
        {
            using var bitmap = new BitmapBuilder(width, height);
            bitmap.Clear(available ? new BitmapColor(20, 65, 85) : new BitmapColor(55, 55, 55));
            bitmap.DrawText(Truncate(title), 3, 2, width - 6, height / 3, fontSize: Math.Max(10, width / 8),
                color: new BitmapColor(255, 255, 255));
            bitmap.DrawText(Truncate(detail), 3, height / 3, width - 6, height * 2 / 3 - 3,
                fontSize: Math.Max(9, width / 9), color: new BitmapColor(255, 255, 255));
            return bitmap.ToImage();
        }

        private static String Truncate(String text) => text.Length > 120 ? text.Substring(0, 117) + "..." : text;
    }
}
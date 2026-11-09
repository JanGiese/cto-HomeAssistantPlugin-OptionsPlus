namespace Loupedeck.HomeAssistantPlugin.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public sealed class MediaPlayerSettings
    {
        public static IReadOnlyList<MediaPlayerControlDefinition> Controls => MediaPlayerControls.Definitions;

        public IReadOnlyList<String> GetVisibleControls(MediaPlayerData? state)
            => MediaPlayerControls.GetEnabledSupported(this, state).Select(x => x.Id).ToArray();

        public MediaPlayerSettings() { }

        public MediaPlayerSettings(String entityId, IEnumerable<String>? enabledControlIds = null)
        {
            this.EntityId = entityId;
            if (enabledControlIds == null)
            {
                return;
            }

            var enabled = new HashSet<String>(enabledControlIds, StringComparer.Ordinal);
            this.PlayPause = enabled.Contains("play_pause");
            this.Previous = enabled.Contains("previous");
            this.Next = enabled.Contains("next");
            this.VolumeDown = enabled.Contains("volume_down");
            this.VolumeUp = enabled.Contains("volume_up");
            this.Mute = enabled.Contains("mute");
            this.Stop = enabled.Contains("stop");
            this.TurnOn = enabled.Contains("turn_on");
            this.TurnOff = enabled.Contains("turn_off");
            this.Source = enabled.Contains("source");
            this.Shuffle = enabled.Contains("shuffle");
            this.Repeat = enabled.Contains("repeat");
        }

        public String EntityId { get; init; } = "";
        public Boolean PlayPause { get; init; } = true;
        public Boolean Previous { get; init; } = true;
        public Boolean Next { get; init; } = true;
        public Boolean VolumeDown { get; init; } = true;
        public Boolean VolumeUp { get; init; } = true;
        public Boolean Mute { get; init; } = true;
        public Boolean Stop { get; init; }
        public Boolean TurnOn { get; init; }
        public Boolean TurnOff { get; init; }
        public Boolean Source { get; init; }
        public Boolean Shuffle { get; init; }
        public Boolean Repeat { get; init; }

        public Boolean IsEnabled(String id) => id switch
        {
            "play_pause" => this.PlayPause,
            "previous" => this.Previous,
            "next" => this.Next,
            "volume_down" => this.VolumeDown,
            "volume_up" => this.VolumeUp,
            "mute" => this.Mute,
            "stop" => this.Stop,
            "turn_on" => this.TurnOn,
            "turn_off" => this.TurnOff,
            "source" => this.Source,
            "shuffle" => this.Shuffle,
            "repeat" => this.Repeat,
            _ => false,
        };

    }

    public sealed record MediaPlayerControlDefinition(String Id, String DisplayName, Boolean DefaultEnabled)
    {
        public String Label => this.DisplayName;
    }

    public static class MediaPlayerControls
    {
        public static IReadOnlyList<MediaPlayerControlDefinition> Definitions { get; } = Array.AsReadOnly(new[]
        {
            new MediaPlayerControlDefinition("play_pause", "Play / Pause", true),
            new MediaPlayerControlDefinition("previous", "Previous", true),
            new MediaPlayerControlDefinition("next", "Next", true),
            new MediaPlayerControlDefinition("volume_down", "Volume Down", true),
            new MediaPlayerControlDefinition("volume_up", "Volume Up", true),
            new MediaPlayerControlDefinition("mute", "Mute", true),
            new MediaPlayerControlDefinition("stop", "Stop", false),
            new MediaPlayerControlDefinition("turn_on", "Turn On", false),
            new MediaPlayerControlDefinition("turn_off", "Turn Off", false),
            new MediaPlayerControlDefinition("source", "Source", false),
            new MediaPlayerControlDefinition("shuffle", "Shuffle", false),
            new MediaPlayerControlDefinition("repeat", "Repeat", false),
        });

        public static IReadOnlyList<MediaPlayerControlDefinition> GetEnabledSupported(
            MediaPlayerSettings settings, MediaPlayerData? state)
            => state == null || settings.EntityId != state.EntityId
                ? Array.Empty<MediaPlayerControlDefinition>()
                : Definitions.Where(x => settings.IsEnabled(x.Id) && IsSupported(x.Id, state)).ToArray();

        public static Boolean IsSupported(String id, MediaPlayerData? state)
        {
            if (state == null || !state.IsAvailable)
            {
                return false;
            }

            var caps = state.Capabilities;
            return id switch
            {
                "play_pause" => state.IsPlaying ? caps.Pause : caps.Play,
                "previous" => caps.Previous,
                "next" => caps.Next,
                "volume_down" or "volume_up" => (caps.VolumeSet && state.VolumeLevel.HasValue) || caps.VolumeStep,
                "mute" => caps.VolumeMute && state.IsVolumeMuted.HasValue,
                "stop" => caps.Stop,
                "turn_on" => caps.TurnOn,
                "turn_off" => caps.TurnOff,
                "source" => caps.SelectSource && state.SourceList.Count > 0,
                "shuffle" => caps.ShuffleSet && state.Shuffle.HasValue,
                "repeat" => caps.RepeatSet && state.Repeat is "off" or "all" or "one",
                _ => false,
            };
        }
    }
}
namespace Loupedeck.HomeAssistantPlugin.Models
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text.Json;

    public sealed record MediaPlayerData
    {
        public String EntityId { get; init; } = "";
        public String FriendlyName { get; init; } = "";
        public String State { get; init; } = "unknown";
        public MediaPlayerCaps Capabilities { get; init; }
        public String? MediaTitle { get; init; }
        public String? MediaArtist { get; init; }
        public Double? VolumeLevel { get; init; }
        public Boolean? IsVolumeMuted { get; init; }
        public String? Source { get; init; }
        public IReadOnlyList<String> SourceList { get; init; } = Array.Empty<String>();
        public Boolean? Shuffle { get; init; }
        public String? Repeat { get; init; }
        public Boolean IsPlaying => String.Equals(this.State, "playing", StringComparison.OrdinalIgnoreCase);
        public Boolean IsAvailable => !String.IsNullOrWhiteSpace(this.State)
            && !new[] { "unknown", "unavailable", "removed" }.Contains(this.State, StringComparer.OrdinalIgnoreCase);

        public static MediaPlayerData? Parse(JsonElement state) => FromState(state);

        public static MediaPlayerData? FromState(JsonElement state)
        {
            if (state.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var entityId = ReadString(state, "entity_id");
            if (entityId == null || !entityId.StartsWith("media_player.", StringComparison.Ordinal)
                || entityId.Length == "media_player.".Length)
            {
                return null;
            }

            state.TryGetProperty("attributes", out var attributes);
            Double? volume = null;
            if (attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty("volume_level", out var level)
                && level.ValueKind == JsonValueKind.Number
                && level.TryGetDouble(out var number) && Double.IsFinite(number))
            {
                volume = Math.Clamp(number, 0, 1);
            }

            var sources = new List<String>();
            if (attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty("source_list", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                sources.AddRange(list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!).Distinct(StringComparer.Ordinal));
            }

            return new MediaPlayerData
            {
                EntityId = entityId,
                FriendlyName = ReadString(attributes, "friendly_name") ?? entityId,
                State = ReadString(state, "state") ?? "unknown",
                Capabilities = MediaPlayerCaps.FromAttributes(attributes),
                MediaTitle = ReadString(attributes, "media_title"),
                MediaArtist = ReadString(attributes, "media_artist"),
                VolumeLevel = volume,
                IsVolumeMuted = ReadBoolean(attributes, "is_volume_muted"),
                Source = ReadString(attributes, "source"),
                SourceList = sources.AsReadOnly(),
                Shuffle = ReadBoolean(attributes, "shuffle"),
                Repeat = ReadString(attributes, "repeat"),
            };
        }

        public static IReadOnlyList<MediaPlayerData> ParseStates(String json)
        {
            using var document = JsonDocument.Parse(json);
            return ParseStates(document.RootElement);
        }

        public static IReadOnlyList<MediaPlayerData> ParseStates(JsonElement states)
        {
            if (states.ValueKind == JsonValueKind.Object && states.TryGetProperty("result", out var result))
            {
                states = result;
            }

            var items = states.ValueKind == JsonValueKind.Array
                ? states.EnumerateArray().ToArray() : new[] { states };
            return items.Select(FromState).OfType<MediaPlayerData>().ToArray();
        }

        private static String? ReadString(JsonElement element, String key)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static Boolean? ReadBoolean(JsonElement element, String key)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                    ? value.GetBoolean() : null;
    }
}
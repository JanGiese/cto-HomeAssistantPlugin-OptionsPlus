namespace Loupedeck.HomeAssistantPlugin.Models
{
    using System.Text.Json;

    public readonly record struct MediaPlayerCaps(Int64 SupportedFeatures)
    {
        public Boolean Pause => this.Has(1);
        public Boolean VolumeSet => this.Has(4);
        public Boolean VolumeMute => this.Has(8);
        public Boolean Previous => this.Has(16);
        public Boolean Next => this.Has(32);
        public Boolean TurnOn => this.Has(128);
        public Boolean TurnOff => this.Has(256);
        public Boolean VolumeStep => this.Has(1024);
        public Boolean SelectSource => this.Has(2048);
        public Boolean Stop => this.Has(4096);
        public Boolean Play => this.Has(16384);
        public Boolean ShuffleSet => this.Has(32768);
        public Boolean RepeatSet => this.Has(262144);

        private Boolean Has(Int64 mask) => (this.SupportedFeatures & mask) != 0;

        public static MediaPlayerCaps FromAttributes(JsonElement attributes)
            => new(attributes.ValueKind == JsonValueKind.Object
                && attributes.TryGetProperty("supported_features", out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out var features) && features >= 0 ? features : 0);
    }
}
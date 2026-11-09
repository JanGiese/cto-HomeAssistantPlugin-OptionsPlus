namespace Loupedeck.HomeAssistantPlugin.Tests.Models;

using System.Text.Json;
using Loupedeck.HomeAssistantPlugin.Models;

public class MediaPlayerDataTests
{
    [Fact]
    public void Parse_PreservesAttributesAndFiltersOtherDomains()
    {
        const string json = """
            [{"entity_id":"light.other","state":"on"},
             {"entity_id":"media_player.office","state":"playing","attributes":{
               "friendly_name":"Office","supported_features":315453,
               "media_title":"Track","media_artist":"Artist","volume_level":0.42,
               "is_volume_muted":false,"source":"Radio","source_list":["Radio","TV",null,2],
               "shuffle":true,"repeat":"all"}}]
            """;

        var state = MediaPlayerData.ParseStates(json).Should().ContainSingle().Subject;
        state.EntityId.Should().Be("media_player.office");
        state.FriendlyName.Should().Be("Office");
        state.IsPlaying.Should().BeTrue();
        state.IsAvailable.Should().BeTrue();
        state.MediaTitle.Should().Be("Track");
        state.MediaArtist.Should().Be("Artist");
        state.VolumeLevel.Should().Be(0.42);
        state.IsVolumeMuted.Should().BeFalse();
        state.Source.Should().Be("Radio");
        state.SourceList.Should().Equal("Radio", "TV");
        state.Shuffle.Should().BeTrue();
        state.Repeat.Should().Be("all");
    }

    [Theory]
    [InlineData("-0.4", 0)]
    [InlineData("1.2", 1)]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    public void Parse_ClampsVolume(string value, double expected)
    {
        var state = MediaPlayerData.ParseStates(
            $$$"""{"entity_id":"media_player.test","state":"idle","attributes":{"volume_level":{{{value}}}}}""")[0];
        state.VolumeLevel.Should().Be(expected);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"invalid\"")]
    [InlineData("{}")]
    public void Parse_UnknownAttributesAreNullableAndDoNotImplyCapabilities(string value)
    {
        var state = MediaPlayerData.ParseStates(
            $$$"""{"entity_id":"media_player.test","attributes":{"volume_level":{{{value}}},"shuffle":"true","is_volume_muted":1}}""")[0];
        state.VolumeLevel.Should().BeNull();
        state.IsVolumeMuted.Should().BeNull();
        state.Shuffle.Should().BeNull();
        state.Repeat.Should().BeNull();
        state.IsAvailable.Should().BeFalse();
        state.Capabilities.Should().Be(default(MediaPlayerCaps));
    }

    [Theory]
    [InlineData("removed", false)]
    [InlineData("unknown", false)]
    [InlineData("unavailable", false)]
    [InlineData("off", true)]
    [InlineData("idle", true)]
    public void Parse_Availability(string state, bool available)
    {
        using var document = JsonDocument.Parse(
            $$"""{"entity_id":"media_player.test","state":"{{state}}","attributes":null}""");
        MediaPlayerData.Parse(document.RootElement)!.IsAvailable.Should().Be(available);
        MediaPlayerData.ParseStates("null").Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, "pause")]
    [InlineData(4, "set")]
    [InlineData(8, "mute")]
    [InlineData(16, "previous")]
    [InlineData(32, "next")]
    [InlineData(128, "on")]
    [InlineData(256, "off")]
    [InlineData(1024, "step")]
    [InlineData(2048, "source")]
    [InlineData(4096, "stop")]
    [InlineData(16384, "play")]
    [InlineData(32768, "shuffle")]
    [InlineData(262144, "repeat")]
    [InlineData(2, "")]
    [InlineData(512, "")]
    [InlineData(65536, "")]
    public void Capabilities_UseIndependentHomeAssistantMasks(long mask, string expected)
    {
        var caps = new MediaPlayerCaps(mask);
        var supported = new Dictionary<string, bool>
        {
            ["pause"] = caps.Pause,
            ["set"] = caps.VolumeSet,
            ["mute"] = caps.VolumeMute,
            ["previous"] = caps.Previous,
            ["next"] = caps.Next,
            ["on"] = caps.TurnOn,
            ["off"] = caps.TurnOff,
            ["step"] = caps.VolumeStep,
            ["source"] = caps.SelectSource,
            ["stop"] = caps.Stop,
            ["play"] = caps.Play,
            ["shuffle"] = caps.ShuffleSet,
            ["repeat"] = caps.RepeatSet,
        };
        supported.Where(x => x.Value).Select(x => x.Key).Should()
            .Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected });
    }

    [Fact]
    public void Settings_DefaultsMatchDefinitionsAndInstancesStayIsolated()
    {
        var defaults = new MediaPlayerSettings("media_player.one");
        foreach (var definition in MediaPlayerControls.Definitions)
        {
            defaults.IsEnabled(definition.Id).Should().Be(definition.DefaultEnabled);
        }

        var ids = new List<string> { "stop" };
        var custom = new MediaPlayerSettings("media_player.two", ids);
        ids.Add("mute");
        custom.Stop.Should().BeTrue();
        custom.Mute.Should().BeFalse();
        custom.PlayPause.Should().BeFalse();
        defaults.Stop.Should().BeFalse();
        defaults.Mute.Should().BeTrue();
        custom.IsEnabled("unknown").Should().BeFalse();
    }

    [Fact]
    public void EnabledSupported_UsesVisibilityCurrentCapabilitiesAndAvailability()
    {
        var settings = new MediaPlayerSettings("media_player.test", new[] { "play_pause", "mute", "next" });
        var state = new MediaPlayerData
        {
            EntityId = settings.EntityId,
            State = "playing",
            Capabilities = new MediaPlayerCaps(1 | 8 | 32),
        };
        MediaPlayerControls.GetEnabledSupported(settings, state).Select(x => x.Id).Should()
            .Equal("play_pause", "next");
        MediaPlayerControls.GetEnabledSupported(settings, state with { State = "paused" })
            .Select(x => x.Id).Should().Equal("next");
        MediaPlayerControls.GetEnabledSupported(settings, state with { State = "unavailable" }).Should().BeEmpty();
        MediaPlayerControls.GetEnabledSupported(settings, null).Should().BeEmpty();
    }
}

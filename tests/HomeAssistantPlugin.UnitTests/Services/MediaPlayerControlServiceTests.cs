namespace Loupedeck.HomeAssistantPlugin.Tests.Services;

using System.Text.Json;
using Loupedeck.HomeAssistantPlugin.Models;
using Loupedeck.HomeAssistantPlugin.Services;

public class MediaPlayerControlServiceTests
{
    private readonly IHaClient _ha = Substitute.For<IHaClient>();
    private MediaPlayerData? _state = new()
    {
        EntityId = "media_player.test",
        State = "playing",
        Capabilities = new MediaPlayerCaps(318909),
        VolumeLevel = 0.5,
        IsVolumeMuted = false,
        Shuffle = false,
        Repeat = "off",
        SourceList = new[] { "Radio", "TV" },
    };
    private readonly MediaPlayerSettings _settings = new("media_player.test",
        MediaPlayerControls.Definitions.Select(x => x.Id));

    public MediaPlayerControlServiceTests()
    {
        this._ha.IsAuthenticated.Returns(true);
        this._ha.CallServiceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>()).Returns((true, null));
    }

    private MediaPlayerControlService Create(int delay = 20) => new(this._ha, _ => this._state, delay);

    [Theory]
    [InlineData("play_pause", "media_pause")]
    [InlineData("previous", "media_previous_track")]
    [InlineData("next", "media_next_track")]
    [InlineData("stop", "media_stop")]
    [InlineData("turn_on", "turn_on")]
    [InlineData("turn_off", "turn_off")]
    public async Task Transport_IsImmediateAndUsesExplicitService(string control, string service)
    {
        using var sut = this.Create(10000);
        var task = sut.ExecuteAsync(this._settings, control);
        task.IsCompleted.Should().BeTrue();
        (await task).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", service, this._settings.EntityId,
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PlayAndPause_RequireTheirSeparateFeatureBits()
    {
        using var sut = this.Create();
        this._state = this._state! with { Capabilities = new MediaPlayerCaps(16384), State = "paused" };
        (await sut.ExecuteAsync(this._settings, "play_pause")).ok.Should().BeTrue();
        this._state = this._state with { State = "playing" };
        (await sut.ExecuteAsync(this._settings, "play_pause")).ok.Should().BeFalse();
        await this._ha.Received(1).CallServiceAsync("media_player", "media_play", this._settings.EntityId,
            null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("mute", "volume_mute", "is_volume_muted", "true")]
    [InlineData("shuffle", "shuffle_set", "shuffle", "true")]
    [InlineData("repeat", "repeat_set", "repeat", "\"all\"")]
    [InlineData("source", "select_source", "source", "\"TV\"")]
    public async Task AttributeCommands_HaveExpectedPayload(string control, string service, string property, string json)
    {
        using var sut = this.Create();
        (await sut.ExecuteAsync(this._settings, control, "TV")).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", service, this._settings.EntityId,
            Arg.Is<JsonElement?>(x => x.HasValue && x.Value.GetProperty(property).GetRawText() == json),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("off", "all")]
    [InlineData("all", "one")]
    [InlineData("one", "off")]
    public async Task Repeat_CyclesKnownModes(string current, string next)
    {
        using var sut = this.Create();
        this._state = this._state! with { Repeat = current };
        (await sut.ExecuteAsync(this._settings, "repeat")).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "repeat_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => x!.Value.GetProperty("repeat").GetString() == next), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HiddenUnsupportedUnavailableUnknownAttributesAndInvalidSources_DoNotSend()
    {
        using var sut = this.Create();
        var hidden = new MediaPlayerSettings(this._settings.EntityId, Array.Empty<string>());
        (await sut.ExecuteAsync(hidden, "play_pause")).ok.Should().BeFalse();
        this._state = this._state! with { IsVolumeMuted = null, Shuffle = null, Repeat = "unknown" };
        foreach (var control in new[] { "mute", "shuffle", "repeat" })
        {
            (await sut.ExecuteAsync(this._settings, control)).ok.Should().BeFalse();
        }

        (await sut.ExecuteAsync(this._settings, "source", "invalid")).ok.Should().BeFalse();
        this._state = this._state with { Capabilities = default };
        (await sut.ExecuteAsync(this._settings, "next")).ok.Should().BeFalse();
        foreach (var state in new[] { "unknown", "unavailable", "removed" })
        {
            this._state = this._state with { State = state };
            (await sut.ExecuteAsync(this._settings, "turn_on")).ok.Should().BeFalse();
        }

        this._state = null;
        (await sut.ExecuteAsync(this._settings, "play_pause")).ok.Should().BeFalse();
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Theory]
    [InlineData(-2, 0)]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task SetVolume_ClampsPayload(double value, double expected)
    {
        using var sut = this.Create();
        (await sut.SetVolumeAsync(this._settings, value)).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => x!.Value.GetProperty("volume_level").GetDouble() == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AbsoluteVolume_DoesNotRequireCurrentLevelButRejectsNonFiniteTargets()
    {
        using var sut = this.Create();
        this._state = this._state! with { VolumeLevel = null, Capabilities = new MediaPlayerCaps(4) };
        (await sut.SetVolumeAsync(this._settings, Double.NaN)).ok.Should().BeFalse();
        (await sut.SetVolumeAsync(this._settings, Double.PositiveInfinity)).ok.Should().BeFalse();
        (await sut.SetVolumeAsync(this._settings, 0.3)).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => x!.Value.GetProperty("volume_level").GetDouble() == 0.3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dial_CoalescesPendingTicksAndPreservesFailureForEveryCaller()
    {
        using var sut = this.Create(40);
        this._ha.CallServiceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>()).Returns((false, "HA error"));
        var first = sut.AdjustVolumeAsync(this._settings, 2);
        var second = sut.AdjustVolumeAsync(this._settings, -1);
        var results = await Task.WhenAll(first, second);
        results.Should().OnlyContain(x => !x.ok && x.error == "HA error");
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => Math.Abs(x!.Value.GetProperty("volume_level").GetDouble() - 0.52) < 0.00001),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0.99, 3, 1)]
    [InlineData(0.01, -3, 0)]
    public async Task Dial_ClampsTwoPercentPerTick(double initial, int ticks, double expected)
    {
        using var sut = this.Create();
        this._state = this._state! with { VolumeLevel = initial };
        (await sut.AdjustVolumeAsync(this._settings, ticks)).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => x!.Value.GetProperty("volume_level").GetDouble() == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HiddenVolumeDirectionAndMismatchedWidget_DoNotSend()
    {
        using var sut = this.Create();
        var onlyDown = new MediaPlayerSettings(this._settings.EntityId, new[] { "volume_down" });
        (await sut.AdjustVolumeAsync(onlyDown, 1)).ok.Should().BeFalse();
        (await sut.SetVolumeAsync(onlyDown, 0.9)).ok.Should().BeFalse();
        (await sut.ExecuteAsync(this._state!, new MediaPlayerSettings("media_player.other"), "next"))
            .ok.Should().BeFalse();
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Fact]
    public async Task VolumeStepOnly_UsesNativeServicesWithoutDebouncing()
    {
        using var sut = this.Create(10000);
        this._state = this._state! with { Capabilities = new MediaPlayerCaps(1024), VolumeLevel = null };
        var task = sut.AdjustVolumeAsync(this._settings, -2);
        task.IsCompleted.Should().BeTrue();
        (await task).ok.Should().BeTrue();
        await this._ha.Received(2).CallServiceAsync("media_player", "volume_down", this._settings.EntityId,
            null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueuedVolume_RechecksCapabilitiesAndAvailability()
    {
        using var sut = this.Create(30);
        var unavailable = sut.AdjustVolumeAsync(this._settings, 1);
        this._state = this._state! with { State = "unavailable" };
        (await unavailable).ok.Should().BeFalse();
        this._state = this._state with { State = "playing", Capabilities = new MediaPlayerCaps(4) };
        var unsupported = sut.AdjustVolumeAsync(this._settings, 1);
        this._state = this._state with { Capabilities = default };
        (await unsupported).ok.Should().BeFalse();
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Fact]
    public async Task LookupCallback_DisconnectionOverridesSuppliedSnapshotDuringDebounce()
    {
        var connected = true;
        using var sut = new MediaPlayerControlService(this._ha, _ => connected ? this._state : null, 30);
        var queued = sut.AdjustVolumeAsync(this._state!, this._settings, 1);
        connected = false;
        (await queued).Should().Be((false, "Media player is unavailable."));
        (await sut.ExecuteAsync(this._state!, this._settings, "next")).ok.Should().BeFalse();
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Fact]
    public async Task RapidCoalescedCalls_AllCompleteSuccessfullyWithoutCancellationErrors()
    {
        using var sut = this.Create(40);
        var calls = Enumerable.Range(0, 5).Select(_ => sut.AdjustVolumeAsync(this._settings, 1)).ToArray();
        var results = await Task.WhenAll(calls);
        results.Should().OnlyContain(x => x.ok && x.error == null);
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => Math.Abs(x!.Value.GetProperty("volume_level").GetDouble() - 0.6) < 0.00001),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_AbortsPendingAndDoesNotAffectOtherEntities()
    {
        using var sut = new MediaPlayerControlService(this._ha, volumeDebounceMs: 30);
        var first = sut.AdjustVolumeAsync(this._state!, this._settings, 1);
        var otherState = this._state! with { EntityId = "media_player.other" };
        var other = sut.AdjustVolumeAsync(otherState, new MediaPlayerSettings(otherState.EntityId), 1);
        sut.CancelPending(this._settings.EntityId);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        (await other).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", otherState.EntityId,
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
        await this._ha.DidNotReceive().CallServiceAsync("media_player", Arg.Any<string>(), this._settings.EntityId,
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreCanceledAndCanceledQueuedRequests_DoNotSend()
    {
        using var sut = this.Create(30);
        using var cts = new CancellationTokenSource();
        var queued = sut.AdjustVolumeAsync(this._settings, 1, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExecuteAsync(this._settings, "next", ct: cts.Token));
        await Task.Delay(60);
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Fact]
    public async Task CanceledCoalescedTick_IsExcludedFromTarget()
    {
        using var sut = this.Create(40);
        using var cts = new CancellationTokenSource();
        var canceled = sut.AdjustVolumeAsync(this._settings, 10, cts.Token);
        var retained = sut.AdjustVolumeAsync(this._settings, -1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        (await retained).ok.Should().BeTrue();
        await this._ha.Received(1).CallServiceAsync("media_player", "volume_set", this._settings.EntityId,
            Arg.Is<JsonElement?>(x => Math.Abs(x!.Value.GetProperty("volume_level").GetDouble() - 0.48) < 0.00001),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_CancelsQueuedVolume()
    {
        var sut = this.Create(10000);
        var queued = sut.AdjustVolumeAsync(this._settings, 1);
        sut.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        this._ha.ReceivedCalls().Should().NotContain(x => x.GetMethodInfo().Name == "CallServiceAsync");
    }

    [Fact]
    public async Task InFlightVolumeCancellation_IsCancellationNotAnErrorTuple()
    {
        using var sut = this.Create();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this._ha.CallServiceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>()).Returns(async info =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, info.ArgAt<CancellationToken>(4));
                return (true, (string?)null);
            });
        var queued = sut.AdjustVolumeAsync(this._settings, 1, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task TransportCancellationWithoutCallerToken_IsNotConvertedToCommandError()
    {
        using var sut = this.Create();
        this._ha.CallServiceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(bool, string?)>(new TaskCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.ExecuteAsync(this._settings, "next"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.AdjustVolumeAsync(this._settings, 1));
    }

    [Fact]
    public async Task ConnectionAndTransportErrors_AreReturned()
    {
        using var sut = this.Create();
        this._ha.IsAuthenticated.Returns(false);
        (await sut.ExecuteAsync(this._settings, "next")).error.Should().Contain("Connection");
        this._ha.IsAuthenticated.Returns(true);
        this._ha.CallServiceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(bool, string?)>(new InvalidOperationException("transport failed")));
        (await sut.ExecuteAsync(this._settings, "next")).Should().Be((false, "transport failed"));
    }
}

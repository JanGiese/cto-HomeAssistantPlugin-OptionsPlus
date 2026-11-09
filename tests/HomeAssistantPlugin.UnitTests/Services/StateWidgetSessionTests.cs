namespace Loupedeck.HomeAssistantPlugin.Tests.Services
{
    using System;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public class StateWidgetSessionTests
    {
        private const String Snapshot = "[{\"entity_id\":\"light.room\",\"state\":\"off\",\"attributes\":{\"friendly_name\":\"Room\"}}]";
        private readonly IHaClient _client = Substitute.For<IHaClient>();
        private readonly IStateWidgetEvents _events = Substitute.For<IStateWidgetEvents>();
        private (String Url, String Token)? _configuration = ("ws://ha", "test-token");

        public StateWidgetSessionTests()
        {
            this._client.IsAuthenticated.Returns(true);
            this._events.IsConnected.Returns(true);
            this._client.ConnectAndAuthenticateAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns((true, "Connected"));
            this._events.ConnectAndSubscribeAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<CancellationToken>()).Returns(true);
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns((true, Snapshot, (String?)null));
            this._client.CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(), Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
                .Returns((true, (String?)null));
        }

        private StateWidgetSession Create() => new(this._client, () => this._configuration, this._events);

        private static StateWidgetSettings Settings(String data = "") =>
            new("light.room", "script.turn_on", "script.other", data, "", "");

        private void Publish(String entity, String? state)
        {
            using var document = JsonDocument.Parse(state == null ? "null" :
                JsonSerializer.Serialize(new { entity_id = entity, state, attributes = new { friendly_name = "Updated" } }));
            this._events.EntityStateChanged += Raise.Event<Action<String, JsonElement?>>(entity,
                state == null ? null : document.RootElement.Clone());
        }

        [Fact]
        public async Task PressCallsOneFixedServiceWithSeparateTargetAndPreservedData()
        {
            using var session = this.Create();
            (await session.RefreshAsync(CancellationToken.None)).Should().BeTrue();

            var result = await session.ExecuteAsync(Settings("{\"value\":5,\"enabled\":true}"), CancellationToken.None);

            result.Ok.Should().BeTrue();
            await this._client.Received(1).CallServiceAsync("script", "turn_on", "script.other",
                Arg.Is<JsonElement?>(data => data.HasValue && data.Value.GetProperty("value").GetInt32() == 5 &&
                    data.Value.GetProperty("enabled").GetBoolean()), Arg.Any<CancellationToken>());
            session.Get("light.room")!.State.Should().Be("off");
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("unavailable")]
        [InlineData(null)]
        public async Task MissingOrUnavailableObservedEntityCannotTriggerServices(String? state)
        {
            using var session = this.Create();
            await session.RefreshAsync(CancellationToken.None);
            this.Publish("light.room", state);

            (await session.ExecuteAsync(Settings(), CancellationToken.None)).Ok.Should().BeFalse();
            await this._client.DidNotReceive().CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task ValidUnmappedStateStillCallsButDisconnectionOrCredentialChangeBlocks()
        {
            using var session = this.Create();
            await session.RefreshAsync(CancellationToken.None);
            this.Publish("light.room", "custom_state");
            (await session.ExecuteAsync(Settings(), CancellationToken.None)).Ok.Should().BeTrue();
            this._events.IsConnected.Returns(false);
            (await session.ExecuteAsync(Settings(), CancellationToken.None)).Ok.Should().BeFalse();
            this._events.IsConnected.Returns(true);
            this._configuration = ("ws://different", "other-token");
            (await session.ExecuteAsync(Settings(), CancellationToken.None)).Ok.Should().BeFalse();

            await this._client.Received(1).CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task SnapshotCannotOverwriteAnEventReceivedDuringRequest()
        {
            using var session = this.Create();
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns(_ =>
            {
                this.Publish("light.room", "on");
                return Task.FromResult((true, (String?)Snapshot, (String?)null));
            });

            await session.RefreshAsync(CancellationToken.None);

            session.Get("light.room")!.State.Should().Be("on");
            session.Get("light.room")!.FriendlyName.Should().Be("Updated");
        }

        [Fact]
        public async Task DeletionDuringSnapshotCannotResurrectAnEntity()
        {
            using var session = this.Create();
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns(_ =>
            {
                this.Publish("light.room", null);
                return Task.FromResult((true, (String?)Snapshot, (String?)null));
            });

            await session.RefreshAsync(CancellationToken.None);

            session.Get("light.room").Should().BeNull();
        }

        [Fact]
        public async Task RequestsAndServicesAreSerialized()
        {
            using var session = this.Create();
            await session.RefreshAsync(CancellationToken.None);
            var pending = new TaskCompletionSource<(Boolean, String?, String?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns(pending.Task);

            var refresh = session.RefreshAsync(CancellationToken.None);
            var execute = session.ExecuteAsync(Settings(), CancellationToken.None);
            execute.IsCompleted.Should().BeFalse();
            await this._client.DidNotReceive().CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
            pending.SetResult((true, Snapshot, null));

            (await refresh).Should().BeTrue();
            (await execute).Ok.Should().BeTrue();
        }

        [Fact]
        public async Task ChangingConfigurationClearsOldEntitiesAndDisposalClosesOnlyOwnedResources()
        {
            var session = this.Create();
            await session.RefreshAsync(CancellationToken.None);
            this._configuration = ("ws://other", "other-token");
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns((true, "[]", (String?)null));
            await session.RefreshAsync(CancellationToken.None);
            session.Entities.Should().BeEmpty();

            this._events.ClearReceivedCalls();
            this._client.ClearReceivedCalls();
            session.Dispose();
            session.Dispose();
            this.Publish("sensor.after_unload", "on");

            session.IsConnected.Should().BeFalse();
            session.Entities.Should().BeEmpty();
            this._events.Received(1).Dispose();
            await this._client.Received(1).SafeCloseAsync();
        }

        [Fact]
        public async Task ReconnectResubscribesAndKeepsEventLifetimeIndependentOfRefresh()
        {
            using var session = this.Create();
            var eventToken = CancellationToken.None;
            this._events.ConnectAndSubscribeAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    eventToken = call.ArgAt<CancellationToken>(2);
                    return true;
                });
            using var refreshLifetime = new CancellationTokenSource();
            await session.RefreshAsync(refreshLifetime.Token);
            refreshLifetime.Cancel();
            eventToken.IsCancellationRequested.Should().BeFalse();

            this._events.IsConnected.Returns(false);
            this._events.ConnectAndSubscribeAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    this._events.IsConnected.Returns(true);
                    return true;
                });
            (await session.RefreshAsync(CancellationToken.None)).Should().BeTrue();
            await this._events.Received(2).ConnectAndSubscribeAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task FailedCallsAndInvalidConfigurationDoNotChangeStateOrRetry()
        {
            using var session = this.Create();
            await session.RefreshAsync(CancellationToken.None);
            var invalid = new StateWidgetSettings("light.room", "invalid", "script.other", "", "", "");
            (await session.ExecuteAsync(invalid, CancellationToken.None)).Ok.Should().BeFalse();
            this._client.CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(), Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
                .Returns((false, "rejected"));
            (await session.ExecuteAsync(Settings(), CancellationToken.None)).Ok.Should().BeFalse();

            session.Get("light.room")!.State.Should().Be("off");
            await this._client.Received(1).CallServiceAsync(Arg.Any<String>(), Arg.Any<String>(), Arg.Any<String>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>());
        }

        [Fact]
        public async Task MissingConfigurationAndMalformedSnapshotsFailClosed()
        {
            using var session = this.Create();
            this._configuration = null;
            (await session.RefreshAsync(CancellationToken.None)).Should().BeFalse();
            session.Status.Should().Be("Configure Home Assistant");
            this._configuration = ("ws://ha", "test-token");
            this._client.RequestAsync("get_states", Arg.Any<CancellationToken>()).Returns((true, "{}", (String?)null));
            (await session.RefreshAsync(CancellationToken.None)).Should().BeFalse();
            session.IsConnected.Should().BeFalse();
        }
    }
}

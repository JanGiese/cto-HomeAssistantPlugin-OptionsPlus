namespace Loupedeck.HomeAssistantPlugin.Tests.Services
{
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Services;

    public class MediaPlayerSessionTests
    {
        [Fact]
        public async Task CancelingOneViewsRefreshDoesNotChangeSharedConnectionStatus()
        {
            using var session = new MediaPlayerSession(Substitute.For<IHaClient>(), () => null);
            await session.RefreshAsync(CancellationToken.None);
            session.Status.Should().Be("Configure Home Assistant");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var result = await session.RefreshAsync(cancellation.Token);

            result.Should().BeFalse();
            session.Status.Should().Be("Configure Home Assistant");
        }

        [Fact]
        public void SnapshotCannotOverwriteAnEventReceivedWhileItWasLoading()
        {
            using var session = new MediaPlayerSession(Substitute.For<IHaClient>(), () => null);
            using var current = JsonDocument.Parse("""{"entity_id":"media_player.kitchen","state":"playing","attributes":{}}""");
            session.OnStateChanged("media_player.kitchen", current.RootElement);

            session.ApplySnapshot("""[{"entity_id":"media_player.kitchen","state":"paused","attributes":{}}]""", 0);

            session.Get("media_player.kitchen")!.State.Should().Be("playing");
        }

        [Fact]
        public void RemovalEventCannotBeUndoneByAnOlderSnapshot()
        {
            using var session = new MediaPlayerSession(Substitute.For<IHaClient>(), () => null);
            session.OnStateChanged("media_player.kitchen", null);

            session.ApplySnapshot("""[{"entity_id":"media_player.kitchen","state":"paused","attributes":{}}]""", 0);

            session.Get("media_player.kitchen").Should().BeNull();
        }

        [Fact]
        public void FreshSnapshotRemovesMissingPlayersAndPreservesOtherEntities()
        {
            using var session = new MediaPlayerSession(Substitute.For<IHaClient>(), () => null);
            session.ApplySnapshot("""[{"entity_id":"media_player.kitchen","state":"paused"},{"entity_id":"media_player.office","state":"idle"}]""", 0);

            session.ApplySnapshot("""[{"entity_id":"media_player.office","state":"playing"}]""", 0);

            session.Get("media_player.kitchen").Should().BeNull();
            session.Players.Should().ContainSingle().Which.EntityId.Should().Be("media_player.office");
        }
    }
}

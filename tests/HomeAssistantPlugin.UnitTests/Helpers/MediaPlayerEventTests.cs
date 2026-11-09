namespace Loupedeck.HomeAssistantPlugin.Tests.Helpers
{
    using System;
    using System.Text.Json;

    public class MediaPlayerEventTests
    {
        [Fact]
        public void StateSnapshotSurvivesIncomingDocumentDisposal()
        {
            using var listener = new HaEventListener();
            JsonElement? snapshot = null;
            listener.MediaPlayerStateChanged += (_, state) => snapshot = state;
            using (var document = JsonDocument.Parse("""{"new_state":{"entity_id":"media_player.kitchen","state":"playing"}}"""))
            {
                listener.PublishMediaPlayerState("media_player.kitchen", document.RootElement);
            }

            snapshot!.Value.GetProperty("state").GetString().Should().Be("playing");
        }

        [Fact]
        public void RemovedEntityIsPublishedEvenWhenAnotherSubscriberThrows()
        {
            using var listener = new HaEventListener();
            var received = false;
            listener.MediaPlayerStateChanged += (_, _) => throw new InvalidOperationException();
            listener.MediaPlayerStateChanged += (entity, state) =>
            {
                entity.Should().Be("media_player.kitchen");
                state.Should().BeNull();
                received = true;
            };
            using var document = JsonDocument.Parse("""{"new_state":null}""");

            listener.PublishMediaPlayerState("media_player.kitchen", document.RootElement);

            received.Should().BeTrue();
        }

        [Fact]
        public void MissingStateDoesNotRemoveAnEntity()
        {
            using var listener = new HaEventListener();
            var received = false;
            listener.MediaPlayerStateChanged += (_, _) => received = true;
            using var document = JsonDocument.Parse("{}");

            listener.PublishMediaPlayerState("media_player.kitchen", document.RootElement);

            received.Should().BeFalse();
        }
    }
}

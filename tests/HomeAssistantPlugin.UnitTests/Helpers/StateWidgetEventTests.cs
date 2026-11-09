namespace Loupedeck.HomeAssistantPlugin.Tests.Helpers
{
    using System;
    using System.Text.Json;

    public class StateWidgetEventTests
    {
        [Theory]
        [InlineData("light.room")]
        [InlineData("switch.room")]
        [InlineData("sensor.room")]
        public void GenericStateEventsOwnSnapshotsAndIsolateSubscribers(String entityId)
        {
            using var listener = new HaEventListener();
            JsonElement? received = null;
            listener.EntityStateChanged += (_, _) => throw new InvalidOperationException();
            listener.EntityStateChanged += (id, state) =>
            {
                id.Should().Be(entityId);
                received = state;
            };
            using (var document = JsonDocument.Parse("{\"new_state\":{\"state\":\"on\"}}"))
            {
                listener.PublishEntityState(entityId, document.RootElement);
            }
            received!.Value.GetProperty("state").GetString().Should().Be("on");
            using (var document = JsonDocument.Parse("{\"new_state\":null}"))
            {
                listener.PublishEntityState(entityId, document.RootElement);
            }
            received.Should().BeNull();
        }
    }
}

namespace Loupedeck.HomeAssistantPlugin.Tests.Actions
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;
    using Loupedeck.HomeAssistantPlugin.Services;

    public class StateWidgetActionTests
    {
        private static StateWidgetSettings Settings(String entity = "light.wohnzimmer", String service = "light.toggle",
            String target = "light.other", String data = "", String icons = "", String fallback = "") =>
            new(entity, service, target, data, icons, fallback);

        [Theory]
        [InlineData("light.flur", "light.flur")]
        [InlineData("sensor.room_temperature", "sensor.room_temperature")]
        [InlineData("", "HA: State Widget")]
        public void DisplayNameUsesConfiguredEntityIdEvenWithoutConnection(String entityId, String expected)
        {
            var action = new StateWidgetAction();
            var parameters = new ActionEditorActionParameters(new Dictionary<String, String>
            {
                [StateWidgetAction.EntityControl] = entityId
            });
            var method = typeof(StateWidgetAction).GetMethod("GetCommandDisplayName", BindingFlags.Instance | BindingFlags.NonPublic)!;

            method.Invoke(action, new Object[] { parameters }).Should().Be(expected);
        }

        [Fact]
        public void EntityDiscoveryRefreshesBothSelectorsWithEntityIds()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var plugin = new HomeAssistantPlugin();
            using var session = plugin.StateWidgets;
            typeof(StateWidgetSession).GetField("_monitor", flags)!.SetValue(session, Task.CompletedTask);
            var action = new StateWidgetAction();

            var initialize = typeof(ActionEditorAction).GetMethod("Initialize", flags)!;
            var listenerInterface = initialize.GetParameters()[1].ParameterType;
            var listenerType = typeof(Plugin).GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => listenerInterface.IsAssignableFrom(type));
            var listener = Activator.CreateInstance(listenerType, flags | BindingFlags.Public, null, new Object[] { plugin }, null);
            initialize.Invoke(action, new[] { plugin, listener });
            var onLoad = typeof(StateWidgetAction).GetMethod("OnLoad", flags)!;
            var onUnload = typeof(StateWidgetAction).GetMethod("OnUnload", flags)!;
            var requestItems = typeof(StateWidgetAction).GetMethod("OnListboxItemsRequested", flags)!;

            onLoad.Invoke(action, null).Should().Be(true);
            var loaded = true;
            try
            {
                var initial = new ActionEditorListboxItemsRequestedEventArgs(null!, StateWidgetAction.EntityControl);
                requestItems.Invoke(action, new Object?[] { null, initial });
                initial.Items.Should().BeEmpty();

                var selection = (String?)null;
                var updates = new List<(String Control, List<ActionEditorListboxItem> Items, String Selected)>();
                plugin.ActionEditorListboxItemsChanged += (_, e) =>
                {
                    var state = selection == null ? null : new ActionEditorState(new[]
                    {
                        new ActionEditorControlState { Name = e.ControlName, Value = selection }
                    });
                    var refreshed = new ActionEditorListboxItemsRequestedEventArgs(state!, e.ControlName);
                    requestItems.Invoke(action, new Object?[] { null, refreshed });
                    updates.Add((e.ControlName, refreshed.Items, refreshed.SelectedItemName));
                };

                const String snapshot = """
                    [
                        {"entity_id":"sensor.garage","state":"10","attributes":{"friendly_name":"Garage"}},
                        {"entity_id":"light.flur","state":"on","attributes":{"friendly_name":"Flur"}}
                    ]
                    """;
                session.ApplySnapshot(snapshot, 0);

                updates.Select(update => update.Control).Should().Equal(
                    StateWidgetAction.EntityControl, StateWidgetAction.TargetControl);
                foreach (var update in updates)
                {
                    update.Items.Select(item => item.DisplayName).Should().Equal("light.flur", "sensor.garage");
                    update.Items.Should().OnlyContain(item => item.Name == item.DisplayName && item.Description == item.Name);
                }

                selection = "light.flur";
                updates.Clear();
                session.ApplySnapshot("""
                    [{"entity_id":"sensor.garage","state":"10","attributes":{"friendly_name":"Garage"}}]
                    """, 0);
                updates.Should().HaveCount(2);
                foreach (var update in updates)
                {
                    update.Selected.Should().Be("light.flur");
                    update.Items.Should().Contain(item =>
                        item.Name == "light.flur" && item.DisplayName == "light.flur" && item.Description == "Currently unavailable");
                }

                plugin.ActionEditorListboxItemsChanged += (_, e) =>
                {
                    if (e.ControlName == StateWidgetAction.EntityControl)
                    {
                        throw new InvalidOperationException("Simulated editor notification failure");
                    }
                };
                updates.Clear();
                session.ApplySnapshot(snapshot, 0);
                updates.Select(update => update.Control).Should().Equal(
                    StateWidgetAction.EntityControl, StateWidgetAction.TargetControl);

                onUnload.Invoke(action, null);
                loaded = false;
                updates.Clear();
                session.ApplySnapshot("[]", 0);
                updates.Should().BeEmpty();
            }
            finally
            {
                if (loaded)
                {
                    onUnload.Invoke(action, null);
                }
            }
        }

        [Fact]
        public void IndependentWidgetsKeepObservedEntityTargetAndTypedDataSeparate()
        {
            var first = StateWidgetAction.ReadSettings(new ActionEditorActionParameters(new Dictionary<String, String>
            {
                [StateWidgetAction.EntityControl] = "sensor.room",
                [StateWidgetAction.ServiceControl] = "light.turn_on",
                [StateWidgetAction.TargetControl] = "light.other",
                [StateWidgetAction.DataControl] = "{\"brightness\":123,\"nested\":{\"enabled\":true,\"values\":[1,null]}}"
            }));
            var second = Settings();

            first.Error.Should().BeNull();
            first.EntityId.Should().Be("sensor.room");
            first.TargetEntityId.Should().Be("light.other");
            first.Data!.Value.GetProperty("brightness").GetInt32().Should().Be(123);
            first.Data.Value.GetProperty("nested").GetProperty("enabled").GetBoolean().Should().BeTrue();
            first.Data.Value.GetProperty("nested").GetProperty("values")[1].ValueKind.Should().Be(JsonValueKind.Null);
            second.Data.Should().BeNull();
            second.EntityId.Should().Be("light.wohnzimmer");
        }

        [Theory]
        [InlineData("light.room", "on", "builtin:light_on")]
        [InlineData("light.room", "off", "builtin:light_off")]
        [InlineData("switch.room", "on", "builtin:switch_on")]
        [InlineData("switch.room", "off", "builtin:switch_off")]
        [InlineData("sensor.room", "on", "builtin:issue")]
        [InlineData("light.room", "ON", "builtin:issue")]
        public void AutomaticIconsUseExactStatesAndAppropriateDomains(String entity, String state, String expected) =>
            Settings(entity: entity).IconFor(state).Should().Be(expected);

        [Fact]
        public void OverridesWinAndUnmappedStatesUseConfiguredFallback()
        {
            var settings = Settings(icons: "{\"on\":\"builtin:script\",\"On\":\"builtin:cover\"}", fallback: "builtin:switch");

            settings.Error.Should().BeNull();
            settings.IconFor("on").Should().Be("builtin:script");
            settings.IconFor("On").Should().Be("builtin:cover");
            settings.IconFor("off").Should().Be("builtin:light_off");
            settings.IconFor("other").Should().Be("builtin:switch");
        }

        [Theory]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("1")]
        [InlineData("{bad")]
        [InlineData("{\"entity_id\":\"light.override\"}")]
        public void InvalidServiceDataIsRejected(String json) =>
            Settings(data: json).Error.Should().NotBeNull();

        [Theory]
        [InlineData("[]")]
        [InlineData("{\"on\":true}")]
        [InlineData("{\"on\":\"builtin:no_such_icon\"}")]
        [InlineData("{\"on\":\"builtin:light_on\",\"on\":\"builtin:light_off\"}")]
        [InlineData("{\"on\":\"relative.png\"}")]
        [InlineData("{\"on\":\"https://example.com/icon.png\"}")]
        public void InvalidIconMappingsAreRejected(String json) =>
            Settings(icons: json).Error.Should().NotBeNull();

        [Theory]
        [InlineData("light", "light.toggle", "light.other")]
        [InlineData("light.room", "toggle", "light.other")]
        [InlineData("light.room", "light.toggle.extra", "light.other")]
        [InlineData("light.room", "light.toggle", "")]
        [InlineData("!loading", "light.toggle", "light.other")]
        public void InvalidRoutingIsRejected(String entity, String service, String target) =>
            Settings(entity, service, target).Error.Should().NotBeNull();

        [Fact]
        public void AbsoluteLocalImagePathsDoNotNeedToExistWhenConfigured()
        {
            var path = Path.Combine(Path.GetTempPath(), "ha-state-widget-missing.svg");
            var settings = Settings(icons: JsonSerializer.Serialize(new Dictionary<String, String> { ["playing"] = path }));

            settings.Error.Should().BeNull();
            settings.IconFor("playing").Should().Be(path);
            Settings(fallback: "relative.svg").Error.Should().NotBeNull();
        }
    }
}

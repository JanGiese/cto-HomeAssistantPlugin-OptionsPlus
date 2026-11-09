namespace Loupedeck.HomeAssistantPlugin.Tests.Actions
{
    using System;
    using System.Collections;
    using System.Reflection;

    public class EntityDisplayNameTests
    {
        [Theory]
        [InlineData(typeof(HomeAssistantLightsDynamicFolder), "_lightsByEntity", "LightItem", "light.flur")]
        [InlineData(typeof(HomeAssistantCoversDynamicFolder), "_coversByEntity", "CoverItem", "cover.flur")]
        [InlineData(typeof(HomeAssistantSwitchesDynamicFolder), "_switchesByEntity", "SwitchItem", "switch.flur")]
        public void DynamicFolderCaptionUsesEntityIdForKnownAndMissingEntities(
            Type folderType, String fieldName, String itemTypeName, String entityId)
        {
            var folder = (PluginDynamicFolder)Activator.CreateInstance(folderType)!;
            var items = (IDictionary)folderType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(folder)!;
            var itemType = folderType.GetNestedType(itemTypeName, BindingFlags.NonPublic)!;
            items.Add(entityId, Activator.CreateInstance(itemType, entityId, "Flur", "off", "", "", "", ""));

            folder.GetCommandDisplayName($"device:{entityId}", default).Should().Be(entityId);
            items.Clear();
            folder.GetCommandDisplayName($"device:{entityId}", default).Should().Be(entityId);
            folder.GetCommandDisplayName("back", default).Should().Be("Back");
            folder.GetCommandDisplayName("retry", default).Should().Be("Retry");
        }

        [Theory]
        [InlineData(typeof(HomeAssistantLightsDynamicFolder))]
        [InlineData(typeof(HomeAssistantCoversDynamicFolder))]
        [InlineData(typeof(HomeAssistantSwitchesDynamicFolder))]
        public void DynamicFolderAreaCaptionsKeepTheirNames(Type folderType)
        {
            var folder = (PluginDynamicFolder)Activator.CreateInstance(folderType)!;
            var areas = (IDictionary)folderType.GetField("_areaIdToName", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(folder)!;
            areas.Add("hallway", "Flur");

            folder.GetCommandDisplayName("areahallway", default).Should().Be("Flur");
        }
    }
}

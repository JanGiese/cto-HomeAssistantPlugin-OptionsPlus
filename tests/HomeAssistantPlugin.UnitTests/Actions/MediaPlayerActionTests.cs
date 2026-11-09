namespace Loupedeck.HomeAssistantPlugin.Tests.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;

    using Loupedeck.HomeAssistantPlugin.Models;

    public class MediaPlayerActionTests
    {
        [Fact]
        public void WidgetSettingsRemainIndependentAndPreserveExplicitFalseValues()
        {
            var first = MediaPlayerAction.ReadSettings(new ActionEditorActionParameters(new Dictionary<String, String>
            {
                [MediaPlayerAction.PlayerControl] = "media_player.kitchen",
                ["play_pause"] = "false",
                ["source"] = "true"
            }));
            var second = MediaPlayerAction.ReadSettings(new ActionEditorActionParameters(new Dictionary<String, String>
            {
                [MediaPlayerAction.PlayerControl] = "media_player.office"
            }));

            first.EntityId.Should().Be("media_player.kitchen");
            first.IsEnabled("play_pause").Should().BeFalse();
            first.IsEnabled("source").Should().BeTrue();
            second.EntityId.Should().Be("media_player.office");
            second.IsEnabled("play_pause").Should().BeTrue();
            second.IsEnabled("source").Should().BeFalse();
        }

        [Fact]
        public async Task OpeningWidgetRequestsSdkFolderNavigationWithoutCreatingAConnection()
        {
            var plugin = new HomeAssistantPlugin();
            var completion = new TaskCompletionSource<ExecuteGenericActionRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            plugin.ExecuteGenericActionRequest += (_, args) => completion.TrySetResult(args);
            var settings = new MediaPlayerSettings("media_player.kitchen", new[] { "play_pause" });

            plugin.OpenMediaPlayer(settings);

            var request = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            request.ActionName.Should().Be(GenericActionNames.DynamicFolder);
            request.ActionParameter.Should().Be(PluginDynamicFolder.DynamicFolderNamePrefix + typeof(MediaPlayerControlsFolder).FullName);
            request.Diff.Should().Be(0);
            plugin.MediaPlayerSelection.Should().BeSameAs(settings);
        }

        [Fact]
        public void SwitchingWidgetsRejectsOldFolderCommandsEvenForTheSamePlayer()
        {
            var folder = new MediaPlayerControlsFolder();
            folder.Configure(new MediaPlayerSettings("media_player.kitchen", new[] { "play_pause" }));
            var oldCommand = folder.Encode("play_pause");
            folder.Configure(new MediaPlayerSettings("media_player.kitchen", new[] { "source" }));

            folder.TryDecode(oldCommand, out _).Should().BeFalse();
            folder.TryDecode(folder.Encode("source"), out var control).Should().BeTrue();
            control.Should().Be("source");
            folder.RunCommand(oldCommand);
        }

        [Fact]
        public void BackUsesSdkNavigationAndClosedFolderIgnoresCommands()
        {
            var plugin = new HomeAssistantPlugin();
            var folder = new MediaPlayerControlsFolder();
            var navigations = new List<String>();
            typeof(PluginDynamicFolder).GetMethod("Init", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(folder, new Object[]
            {
                plugin,
                (Action<String, String, DeviceControlType>)((_, _, _) => { }),
                (Action<String, String, String>)((_, _, _) => { }),
                (Action<String, String, String>)((_, _, _) => { }),
                (Action<String, String, Int32>)((action, _, _) => navigations.Add(action))
            });
            folder.Configure(new MediaPlayerSettings("media_player.kitchen"));
            folder.Activate();

            var controls = folder.GetButtonPressActionNames(DeviceType.None)
                .Select(action => ActionString.FromString(action).ActionParameter).ToArray();
            controls.Should().HaveCount(3);
            controls.Should().Contain(folder.Encode("back"));
            controls.Should().Contain(folder.Encode("status"));
            controls.Should().Contain(folder.Encode("retry"));
            folder.RunCommand(folder.Encode("back"));
            navigations.Should().ContainSingle().Which.Should().Be(GenericActionNames.DynamicFolderGoUp);

            folder.Deactivate();
            folder.RunCommand(folder.Encode("back"));
            navigations.Should().ContainSingle();
            folder.Unload();
        }
    }
}

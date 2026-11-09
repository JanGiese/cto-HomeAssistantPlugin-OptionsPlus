using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using Loupedeck.HomeAssistantPlugin.Services;

using Xunit;

namespace Loupedeck.HomeAssistantPlugin.Tests.Native;

public sealed class NativeRenderingTests
{
    [NativeMacFact]
    [Trait("Category", "Native")]
    public void SdkGraphics_OnArm64_LoadsImagesAndRendersFallbacks()
    {
        Assert.True(OperatingSystem.IsMacOS());
        Assert.Equal(Architecture.Arm64, RuntimeInformation.ProcessArchitecture);

        var provider = new PluginResourceProvider(typeof(HomeAssistantPlugin).Assembly);
        Assert.NotNull(provider.LoadImage("light_bulb_icon.svg"));
        Assert.NotNull(provider.LoadImage("online_status_icon.png"));
        var icons = new IconService(new Dictionary<String, String>
        {
            ["svg"] = "light_bulb_icon.svg",
            ["png"] = "online_status_icon.png"
        });
        Assert.NotNull(icons.Get("svg"));
        Assert.NotNull(icons.Get("png"));
        Assert.NotNull(icons.Get("missing"));

        using var bitmap = new BitmapBuilder(80, 80);
        bitmap.DrawText("?", fontSize: 32, color: new BitmapColor(255, 255, 255));
        Assert.NotNull(TilePainter.IconOrGlyph(bitmap, null, "?"));
    }

    [NativeMacFact]
    [Trait("Category", "Native")]
    public void StateWidget_LoadsLocalImagesAndFallsBackForMissingOrCorruptFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ha-state-widget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PluginResources.Init(typeof(HomeAssistantPlugin).Assembly);
            var svg = Path.Combine(directory, "custom.svg");
            var png = Path.Combine(directory, "custom.png");
            PluginResources.ExtractFile("light_on_icon.svg", svg);
            PluginResources.ExtractFile("online_status_icon.png", png);
            var resolver = new StateWidgetIconResolver();
            foreach (var reference in new[] { "builtin:light_on", svg, png })
            {
                var image = resolver.Get(reference, "builtin:issue");
                Assert.NotNull(image);
                var tile = StateWidgetAction.Draw(100, 80, image, "on", true);
                Assert.Equal(100, tile.Width);
                Assert.Equal(80, tile.Height);
            }
            var fallback = resolver.Get("builtin:issue", "builtin:issue");
            Assert.Same(fallback, resolver.Get(Path.Combine(directory, "missing.svg"), "builtin:issue"));
            File.WriteAllText(svg, "not an SVG");
            Assert.Same(fallback, resolver.Get(svg, "builtin:issue"));
            File.Delete(png);
            Assert.Same(fallback, resolver.Get(png, "builtin:issue"));
            Assert.NotNull(StateWidgetAction.Draw(80, 80, fallback, "Unavailable", false));
            resolver.Clear();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class NativeMacFactAttribute : FactAttribute
    {
        public NativeMacFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RUN_NATIVE_SMOKE") != "1")
            {
                this.Skip = "Opt in on an arm64 Mac with RUN_NATIVE_SMOKE=1 and -p:EnableNativeSmokeTests=true.";
            }
        }
    }
}

using Bunit;
using Concierge.Shared.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Tests;

public sealed class CircleAiServiceSettingsTests : BunitContext
{
    [Fact]
    public async Task Both_default_on_and_choices_survive_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var preferences = new CircleAiServicePreferences(path);
            Assert.Equal(new CircleAiServiceOptions(true, true), preferences.Current);
            await preferences.SaveAsync(new(false, false));
            Assert.Equal(new CircleAiServiceOptions(false, false), new CircleAiServicePreferences(path).Current);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Overlapping_toggles_do_not_overwrite_each_other()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var preferences = new CircleAiServicePreferences(path);
            await Task.WhenAll(
                preferences.UpdateAsync(current => current with { StreamAnswers = false }),
                preferences.UpdateAsync(current => current with { AgenticWork = false }));
            Assert.Equal(new CircleAiServiceOptions(false, false), new CircleAiServicePreferences(path).Current);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Failed_save_keeps_the_previous_choices()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var preferences = new CircleAiServicePreferences(path);
        Directory.CreateDirectory(path + ".tmp"); // Write must fail, not pretend to save.
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => preferences.SaveAsync(new(false, false)));
            Assert.Equal(new CircleAiServiceOptions(), preferences.Current);
        }
        finally { Directory.Delete(path + ".tmp"); }
    }

    [Fact]
    public void Settings_show_real_controls_and_the_unavailable_agentic_limitation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var preferences = new CircleAiServicePreferences(path);
            Services.AddSingleton(preferences);
            var panel = Render<Concierge.Shared.Components.Components.CircleAiServiceSettings>();
            Assert.All(panel.FindAll("input"), input => Assert.True(input.HasAttribute("checked")));
            Assert.Contains("Your preference is saved for when it becomes available", panel.Markup);
            panel.FindAll("input")[0].Change(false);
            panel.WaitForAssertion(() => Assert.False(preferences.Current.StreamAnswers));
            panel.FindAll("input")[1].Change(false);
            panel.WaitForAssertion(() => Assert.False(preferences.Current.AgenticWork));
            Assert.Equal(new CircleAiServiceOptions(false, false), new CircleAiServicePreferences(path).Current);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Heads_without_the_service_do_not_show_nonfunctional_controls()
        => Assert.Empty(Render<Concierge.Shared.Components.Components.CircleAiServiceSettings>().FindAll("input"));
}

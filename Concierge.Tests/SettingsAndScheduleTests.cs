using System.Text.Json;
using Concierge.Shared.Settings;

namespace Concierge.Tests;

/// <summary>
/// What hot-reloading settings must do (parity feature 38): pick up a change without the app
/// being restarted.
/// </summary>
/// <remarks>
/// On Android a restart costs the model load and the warm KV cache — several seconds of the
/// user staring at a loading state because they changed a toggle. Reading the file again is
/// cheaper than any of that.
/// </remarks>
public sealed class HotReloadingSettingsTests : IDisposable
{
    private sealed record Settings(string Voice, bool KidMode);

    private readonly string _path;

    public HotReloadingSettingsTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"concierge-settings-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Disposable temp file.
        }
    }

    [Fact]
    public void Missing_settings_fall_back_to_the_default()
    {
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));

        Assert.Equal("default", settings.Current.Voice);
    }

    [Fact]
    public void Settings_are_read_from_the_file()
    {
        Write(new Settings("isiZulu", true));
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));

        Assert.Equal("isiZulu", settings.Current.Voice);
    }

    [Fact]
    public void A_change_on_disk_is_picked_up_without_a_restart()
    {
        Write(new Settings("English", false));
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));
        _ = settings.Current;

        Write(new Settings("Sesotho", true));
        settings.Reload();

        Assert.Equal("Sesotho", settings.Current.Voice);
    }

    [Fact]
    public void A_reader_is_told_when_settings_change()
    {
        Write(new Settings("English", false));
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));
        var notified = 0;
        settings.Changed += (_, _) => notified++;

        Write(new Settings("Afrikaans", false));
        settings.Reload();

        Assert.Equal(1, notified);
    }

    [Fact]
    public void A_reload_that_changes_nothing_does_not_disturb_readers()
    {
        Write(new Settings("English", false));
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));
        var notified = 0;
        settings.Changed += (_, _) => notified++;

        settings.Reload();

        Assert.Equal(0, notified);
    }

    [Fact]
    public void A_corrupt_file_keeps_the_settings_that_were_working()
    {
        // Half-written JSON must not reset a parent's Kid Mode choice to the default.
        Write(new Settings("isiZulu", true));
        var settings = new HotReloadingSettings<Settings>(_path, () => new Settings("default", false));
        _ = settings.Current;

        File.WriteAllText(_path, "{ broken");
        settings.Reload();

        Assert.True(settings.Current.KidMode);
    }

    private void Write(Settings settings)
        => File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

/// <summary>
/// What scheduling must do (parity feature 45): run something later without the app being
/// open at the time.
/// </summary>
/// <remarks>
/// The store is the part worth owning. Whether the wake-up comes from a MAUI background task,
/// a Windows service, or a phone that was simply reopened, what must survive is the record of
/// what was due and whether it ran.
/// </remarks>
public sealed class ScheduledTaskTests : IDisposable
{
    private readonly string _path;

    public ScheduledTaskTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"concierge-schedule-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // Disposable temp file.
        }
    }

    [Fact]
    public void A_scheduled_task_is_kept()
    {
        var store = new FileScheduledTaskStore(_path);

        store.Schedule("summarise the day", DateTimeOffset.UtcNow.AddHours(1));

        Assert.Single(store.All());
    }

    [Fact]
    public void A_task_that_is_not_due_yet_is_not_returned()
    {
        var store = new FileScheduledTaskStore(_path);
        store.Schedule("later", DateTimeOffset.UtcNow.AddHours(1));

        Assert.Empty(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_task_that_is_due_is_returned()
    {
        var store = new FileScheduledTaskStore(_path);
        store.Schedule("now", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.Single(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_task_that_has_run_is_no_longer_due()
    {
        var store = new FileScheduledTaskStore(_path);
        var task = store.Schedule("now", DateTimeOffset.UtcNow.AddMinutes(-1));

        store.MarkRan(task.Id, DateTimeOffset.UtcNow);

        Assert.Empty(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_repeating_task_becomes_due_again()
    {
        var store = new FileScheduledTaskStore(_path);
        var task = store.Schedule("every day", DateTimeOffset.UtcNow.AddMinutes(-1), TimeSpan.FromDays(1));

        store.MarkRan(task.Id, DateTimeOffset.UtcNow);

        Assert.Single(store.Due(DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1)));
    }

    [Fact]
    public void Scheduled_tasks_survive_a_restart()
    {
        new FileScheduledTaskStore(_path).Schedule("survive", DateTimeOffset.UtcNow.AddHours(1));

        Assert.Single(new FileScheduledTaskStore(_path).All());
    }

    [Fact]
    public void A_task_that_was_missed_while_the_app_was_closed_is_still_due()
    {
        // The phone was off when it fell due. Skipping it silently is how a reminder
        // becomes something the user stops trusting.
        var store = new FileScheduledTaskStore(_path);
        store.Schedule("missed", DateTimeOffset.UtcNow.AddDays(-3));

        Assert.Single(store.Due(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_cancelled_task_is_gone()
    {
        var store = new FileScheduledTaskStore(_path);
        var task = store.Schedule("never mind", DateTimeOffset.UtcNow.AddHours(1));

        store.Cancel(task.Id);

        Assert.Empty(store.All());
    }

    [Fact]
    public void An_empty_description_is_refused()
    {
        Assert.Throws<ArgumentException>(
            () => new FileScheduledTaskStore(_path).Schedule("  ", DateTimeOffset.UtcNow));
    }
}

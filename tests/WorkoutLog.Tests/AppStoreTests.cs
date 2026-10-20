using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Tests;

public sealed class AppStoreTests
{
    [Fact]
    public async Task Bulk_entry_persists_all_unfinished_sets_without_inventing_reserve_or_completion()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        var id = store.CurrentSession!.Id;
        Assert.True(await store.FillRemainingSetsAsync(id, 5, "60,5", "10"));

        var reloaded = CreateStore(browser);
        await reloaded.InitializeAsync();
        Assert.All(reloaded.CurrentSession!.Exercises[0].Sets, set =>
        {
            Assert.Equal(60.5m, set.Weight);
            Assert.Equal("60,5", set.WeightInput);
            Assert.Equal(10, set.Repetitions);
            Assert.Null(set.Reserve);
            Assert.False(set.Done);
        });
        Assert.Empty(reloaded.State.Outbox);
        Assert.Null(reloaded.CurrentSession.FinishedAt);
    }

    [Fact]
    public async Task Bulk_entry_preserves_completed_results_and_each_sets_reserve()
    {
        var store = CreateStore(new StorageJavascript());
        await store.InitializeAsync();
        await store.StartAsync();
        var id = store.CurrentSession!.Id;
        await store.MutateAsync(state =>
        {
            state.Sessions[0].Exercises[0].Sets[0] = new SetResult
            {
                Weight = 50, WeightInput = "50", Repetitions = 8, Reserve = 2, Done = true
            };
            state.Sessions[0].Exercises[0].Sets[1].Reserve = 1;
        });
        Assert.True(await store.FillRemainingSetsAsync(id, 5, "65", "12"));
        var sets = store.CurrentSession!.Exercises[0].Sets;
        Assert.Equal(50, sets[0].Weight);
        Assert.Equal(8, sets[0].Repetitions);
        Assert.Equal(2, sets[0].Reserve);
        Assert.True(sets[0].Done);
        Assert.Equal(65, sets[1].Weight);
        Assert.Equal(12, sets[1].Repetitions);
        Assert.Equal(1, sets[1].Reserve);
        Assert.False(sets[1].Done);

        await store.UpdateSetAsync(id, 5, 1, "reps", "11");
        Assert.Equal(11, store.CurrentSession.Exercises[0].Sets[1].Repetitions);
    }

    [Theory]
    [InlineData("-1", "10")]
    [InlineData("50", "0")]
    [InlineData("50", "10,5")]
    [InlineData("", "10")]
    [InlineData("2001", "10")]
    [InlineData("50", "501")]
    public async Task Invalid_bulk_input_never_partially_changes_saved_sets(string weight, string repetitions)
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        var id = store.CurrentSession!.Id;
        await store.FillRemainingSetsAsync(id, 5, "40", "8");
        var revision = browser.Stored.Revision;
        Assert.False(await store.FillRemainingSetsAsync(id, 5, weight, repetitions));
        Assert.Equal(revision, browser.Stored.Revision);
        Assert.All(store.CurrentSession.Exercises[0].Sets, set =>
        {
            Assert.Equal(40, set.Weight);
            Assert.Equal(8, set.Repetitions);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bulk_entry_cannot_modify_finished_or_skipped_exercise(bool finished)
    {
        var store = CreateStore(new StorageJavascript());
        await store.InitializeAsync();
        await store.StartAsync();
        await store.MutateAsync(state =>
        {
            state.Sessions[0].FinishedAt = finished ? DateTimeOffset.UtcNow : null;
            state.Sessions[0].Exercises[0].Skipped = !finished;
        });
        Assert.False(await store.FillRemainingSetsAsync(store.CurrentSession!.Id, 5, "60", "10"));
        Assert.All(store.CurrentSession.Exercises[0].Sets, set => Assert.Null(set.Weight));
    }

    [Fact]
    public async Task Failed_bulk_save_keeps_all_previous_values()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        await store.FillRemainingSetsAsync(store.CurrentSession!.Id, 5, "0", "12");
        browser.FailWrites = true;
        Assert.False(await store.FillRemainingSetsAsync(store.CurrentSession.Id, 5, "50", "8"));
        Assert.All(browser.Stored.Sessions[0].Exercises[0].Sets, set => Assert.Equal(0, set.Weight));
        Assert.All(store.CurrentSession.Exercises[0].Sets, set => Assert.Equal(12, set.Repetitions));
    }

    [Fact]
    public async Task Progression_settings_and_technique_survive_reload_and_backup_merge()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        await store.MutateAsync(state =>
        {
            state.ProgressionSettings["exercise"] = new ExerciseProgressionSettings { WeightStep = 1.25m };
            state.Sessions[0].Exercises[0].TechniqueMaintained = false;
        });
        var reloaded = CreateStore(browser);
        await reloaded.InitializeAsync();
        Assert.Equal(1.25m, reloaded.State.ProgressionSettings["exercise"].WeightStep);
        Assert.False(reloaded.CurrentSession!.Exercises[0].TechniqueMaintained);

        var destination = CreateStore(new StorageJavascript());
        await destination.InitializeAsync();
        await destination.MutateAsync(state =>
            state.ProgressionSettings["exercise"] = new ExerciseProgressionSettings { WeightStep = 5 });
        await destination.ImportAsync(JsonSerializer.Serialize(reloaded.State));
        Assert.Equal(5, destination.State.ProgressionSettings["exercise"].WeightStep);
        Assert.False(destination.State.Sessions[0].Exercises[0].TechniqueMaintained);

        var fresh = CreateStore(new StorageJavascript());
        await fresh.InitializeAsync();
        await fresh.ImportAsync(JsonSerializer.Serialize(reloaded.State));
        Assert.Equal(1.25m, fresh.State.ProgressionSettings["exercise"].WeightStep);
    }

    [Fact]
    public async Task Decimal_separator_survives_incremental_mobile_typing_and_reload()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        var session = store.CurrentSession!;
        await store.UpdateSetAsync(session.Id, 5, 0, "weight", "60,");
        Assert.Equal("60,", store.CurrentSession!.Exercises[0].Sets[0].WeightInput);
        await store.UpdateSetAsync(session.Id, 5, 0, "weight", "60,5");

        var reloaded = CreateStore(browser);
        await reloaded.InitializeAsync();
        Assert.Equal(60.5m, reloaded.CurrentSession!.Exercises[0].Sets[0].Weight);
        Assert.Equal("60,5", reloaded.CurrentSession.Exercises[0].Sets[0].WeightInput);
    }

    [Fact]
    public async Task Completion_persists_session_and_queue_together_without_google_login()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        var id = store.CurrentSession!.Id;
        await store.UpdateSetAsync(id, 5, 0, "weight", "60");
        await store.UpdateSetAsync(id, 5, 0, "reps", "10");
        await store.UpdateSetAsync(id, 5, 0, "reserve", "2");
        await store.ToggleSetAsync(id, 5, 0);
        Assert.False(store.CurrentSession!.Exercises[0].Sets[1].Done);
        await store.ToggleSetAsync(id, 5, 1);
        await store.CompleteAsync(id);

        var restored = CreateStore(browser);
        await restored.InitializeAsync();
        Assert.NotNull(restored.CurrentSession!.FinishedAt);
        Assert.Null(restored.CurrentSession.SyncedAt);
        Assert.Single(restored.State.Outbox);
        Assert.Equal(UploadStatus.NeedsSignIn, restored.State.Outbox[0].Status);
    }

    [Fact]
    public async Task Stale_tab_cannot_overwrite_a_newer_revision()
    {
        var browser = new StorageJavascript();
        var first = CreateStore(browser);
        var second = CreateStore(browser);
        await first.InitializeAsync();
        await second.InitializeAsync();
        await first.MutateAsync(state => state.SelectedWeek = 2);
        Assert.False(await second.MutateAsync(state => state.SelectedWeek = 1));
        Assert.True(second.StorageFailed);
        Assert.Equal(2, browser.Stored.SelectedWeek);
    }

    [Fact]
    public async Task Storage_failure_never_claims_a_set_was_saved()
    {
        var browser = new StorageJavascript();
        var store = CreateStore(browser);
        await store.InitializeAsync();
        await store.StartAsync();
        var revision = browser.Stored.Revision;
        browser.FailWrites = true;
        Assert.False(await store.UpdateSetAsync(store.CurrentSession!.Id, 5, 0, "weight", "99"));
        Assert.True(store.StorageFailed);
        Assert.Equal(revision, browser.Stored.Revision);
        Assert.Null(store.CurrentSession.Exercises[0].Sets[0].Weight);
    }

    private static AppStore CreateStore(StorageJavascript javascript)
    {
        var configuration = new ConfigurationBuilder().Build();
        var storage = new BrowserStorage(javascript);
        var gateway = new GoogleSheetsGateway(new HttpClient(new RejectNetwork()), javascript);
        return new AppStore(storage, gateway, javascript, configuration);
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Этот сценарий не должен обращаться к сети.");
        }
    }

    private sealed class StorageJavascript : IJSRuntime
    {
        public LocalState Stored { get; private set; } = new() { Plan = PlanAndQueueTests.Plan() };
        public bool FailWrites { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            object? result = null;
            switch (identifier)
            {
                case "workout.storage.read":
                    result = Clone(Stored);
                    break;
                case "workout.storage.write":
                    if (FailWrites)
                    {
                        throw new JSException("QuotaExceededError");
                    }
                    var next = (LocalState)args![0]!;
                    var expectedRevision = (int)args[1]!;
                    if (expectedRevision != Stored.Revision)
                    {
                        throw new JSException("STALE_REVISION");
                    }
                    Stored = Clone(next);
                    Stored.Revision++;
                    result = Stored.Revision;
                    break;
                case "workout.initialize":
                    result = true;
                    break;
                case "workout.pwa.ready":
                case "workout.storage.isPersistent":
                    result = false;
                    break;
            }
            return ValueTask.FromResult(result is null ? default! : (TValue)result);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            return InvokeAsync<TValue>(identifier, args);
        }

        private static LocalState Clone(LocalState state)
        {
            return JsonSerializer.Deserialize<LocalState>(JsonSerializer.Serialize(state))!;
        }
    }
}

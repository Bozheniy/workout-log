using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.JSInterop;
using WorkoutLog.Core;
using WorkoutLog.Web.Services;

namespace WorkoutLog.Tests;

public sealed class AppStoreTests
{
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

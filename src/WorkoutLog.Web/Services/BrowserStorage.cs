using Microsoft.JSInterop;
using WorkoutLog.Core;

namespace WorkoutLog.Web.Services;

public sealed class BrowserStorage(IJSRuntime javascript)
{
    public ValueTask<LocalState?> LoadAsync()
    {
        return javascript.InvokeAsync<LocalState?>("workout.storage.read");
    }

    public async Task SaveAsync(LocalState state)
    {
        // Проверка версии и запись выполняются в одной транзакции IndexedDB.
        // Это не позволяет второй вкладке молча затереть более свежие данные.
        var nextRevision = await javascript.InvokeAsync<int>("workout.storage.write", state, state.Revision);
        state.Revision = nextRevision;
    }
}

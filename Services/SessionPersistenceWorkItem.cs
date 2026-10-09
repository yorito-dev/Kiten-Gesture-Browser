using TabBrowser.Models;

namespace TabBrowser.Services;

internal sealed record SessionPersistenceWorkItem(
    SessionData Snapshot,
    Func<Task> SaveAsync,
    Action SaveSynchronously);

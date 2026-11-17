namespace TabBrowser.Services;

internal static class UiOperationExceptionBoundary
{
    internal static async Task RunAsync(
        Func<Task> operation,
        Action<Exception> recordFailure,
        Action showFailureStatus)
    {
        try
        {
            await operation();
        }
        catch (Exception exception) when (IsRecoverableOperationFailure(exception))
        {
            recordFailure(exception);
            showFailureStatus();
        }
    }

    internal static void Run(
        Action operation,
        Action<Exception> recordFailure,
        Action showFailureStatus)
    {
        try
        {
            operation();
        }
        catch (Exception exception) when (IsRecoverableOperationFailure(exception))
        {
            recordFailure(exception);
            showFailureStatus();
        }
    }

    private static bool IsRecoverableOperationFailure(Exception exception)
        => exception is not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;
}

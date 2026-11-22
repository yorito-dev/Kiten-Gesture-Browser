using System.Windows;
using System.Windows.Input;
using TabBrowser.Services;

namespace TabBrowser.ViewModels;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return _canExecute?.Invoke(parameter) ?? true;
    }

    public void Execute(object? parameter)
    {
        _execute(parameter);
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception> _recordFailure;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        : this(execute, canExecute, static exception =>
            (Application.Current as App)?.RecordUiOperationException("async-relay-command", exception, includeStackTrace: false))
    {
    }

    internal AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute, Action<Exception> recordFailure)
    {
        _execute = execute;
        _canExecute = canExecute;
        _recordFailure = recordFailure;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isExecuting && (_canExecute?.Invoke(parameter) ?? true);
    }

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync(parameter);
        }
        catch (OperationCanceledException)
        {
            // キャンセルはコマンドの正常な終了として扱う。
        }
        catch (Exception exception) when (UiOperationExceptionBoundary.IsRecoverableOperationFailure(exception))
        {
            _recordFailure(exception);
        }
    }

    internal async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

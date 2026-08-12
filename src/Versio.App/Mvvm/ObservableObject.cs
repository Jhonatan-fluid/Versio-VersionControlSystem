using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Versio.App.Mvvm;

/// <summary>
/// Minimal MVVM base.
/// </summary>
/// <remarks>
/// The plan named CommunityToolkit.Mvvm. What is actually needed here is property change
/// notification and an async command that disables itself while running — about eighty
/// lines — so this keeps the app at zero third-party dependencies. If the view models grow
/// enough to want source generators, the toolkit drops in without changing any view.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>
/// An async command that cannot be double-invoked.
/// </summary>
/// <remarks>
/// <see cref="IsRunning"/> gates <see cref="CanExecute"/>, so the button disables itself for
/// the duration. A Push button that can be double-clicked into two concurrent operations is
/// a defect, and for the CODESYS commands it would mean two competing headless instances.
/// </remarks>
public sealed class AsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        IsRunning = true;
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // An unhandled exception from an async void would tear down the process. The
            // command surfaces it instead, so a failed git push shows an error rather than
            // closing the app.
            onError?.Invoke(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An async command that acts on the thing that invoked it.
/// </summary>
/// <remarks>
/// <para>
/// The parameterless <see cref="AsyncCommand"/> covers a button whose meaning is fixed. This
/// covers one repeated across rows, where the row is the argument — staging a particular hunk
/// of a diff, which is the case it was written for.
/// </para>
/// <para>
/// Sharing one instance across every row is the point: a command per row would mean building
/// one per hunk of a 33,644-line diff, and the recycling the diff view depends on would then
/// be handing containers commands that belonged to a line they no longer show.
/// </para>
/// </remarks>
public sealed class AsyncCommand<T>(
    Func<T, Task> execute,
    Func<T, bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        !_isRunning && parameter is T typed && (canExecute?.Invoke(typed) ?? true);

    public async void Execute(object? parameter)
    {
        if (parameter is not T typed || !CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();

        try
        {
            await execute(typed).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Synchronous command, for UI-only actions such as toggling a panel.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

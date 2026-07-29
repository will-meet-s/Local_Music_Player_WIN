using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MusicCore.ViewModels;

/// <summary>INotifyPropertyChanged 样板。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>值真的变了才通知，避免无谓重绘。</summary>
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>最简命令实现，够 WPF 按钮绑定用。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>带参数的命令，列表双击播放用得上。</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute) => _execute = execute;

    /// <summary>
    /// CanExecute 恒为 true，没有会变化的状态，所以这个事件永远不需要触发。
    /// 空访问器满足 ICommand 契约，同时消掉 CS0067（声明了事件却从不使用）。
    /// 哪天这个命令加上 canExecute 谓词，就换回自动事件并补 RaiseCanExecuteChanged。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
        if (parameter is T value) _execute(value);
    }
}

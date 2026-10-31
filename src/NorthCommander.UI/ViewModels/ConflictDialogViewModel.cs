using System.Globalization;
using Avalonia.Threading;
using NorthCommander.Core.Transfers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NorthCommander.UI.ViewModels;

/// <summary>
/// Overwrite / Skip / Rename prompt shown when a transfer hits an existing file. Workers wait on
/// <see cref="ResolveAsync"/> while the UI shows one conflict at a time.
/// </summary>
public sealed partial class ConflictDialogViewModel : ViewModelBase, IFileConflictResolver
{
    private readonly Queue<Request> _pending = new();
    private Request? _current;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FileName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ExistingDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NewDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RenameHint { get; private set; } = string.Empty;

    public Task<FileConflictResolution> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
    {
        var request = new Request(conflict);
        request.Registration = cancellationToken.Register(() =>
        {
            request.Completion.TrySetCanceled(cancellationToken);
            Dispatcher.UIThread.Post(ShowNext);
        });

        Dispatcher.UIThread.Post(() =>
        {
            _pending.Enqueue(request);
            ShowNext();
        });

        return request.Completion.Task;
    }

    [RelayCommand]
    private void Overwrite() => Answer(FileConflictResolution.Overwrite);

    [RelayCommand]
    private void OverwriteAll() => Answer(FileConflictResolution.OverwriteAll);

    [RelayCommand]
    private void Skip() => Answer(FileConflictResolution.Skip);

    [RelayCommand]
    private void SkipAll() => Answer(FileConflictResolution.SkipAll);

    [RelayCommand]
    private void Rename() => Answer(FileConflictResolution.Rename);

    [RelayCommand]
    private void Cancel() => Answer(FileConflictResolution.Cancel);

    private void Answer(FileConflictResolution resolution)
    {
        if (_current is not { } request)
        {
            return;
        }

        _current = null;
        request.Registration.Dispose();
        request.Completion.TrySetResult(resolution);
        ShowNext();
    }

    private void ShowNext()
    {
        if (_current is { Completion.Task.IsCompleted: false })
        {
            return;
        }

        _current = null;
        while (_pending.TryDequeue(out var next))
        {
            if (!next.Completion.Task.IsCompleted)
            {
                _current = next;
                break;
            }
        }

        if (_current is not { Conflict: var conflict })
        {
            IsOpen = false;
            return;
        }

        var verb = conflict.Task.TaskType == FileTransferType.Move ? "Move" : "Copy";
        Title = $"{verb}: file already exists";
        FileName = Path.GetFileName(conflict.DestinationPath);
        TargetFolder = Path.GetDirectoryName(conflict.DestinationPath) ?? string.Empty;
        ExistingDetails = Describe(conflict.DestinationSize, conflict.DestinationModified, conflict.DestinationModified > conflict.SourceModified);
        NewDetails = Describe(conflict.SourceSize, conflict.SourceModified, conflict.SourceModified > conflict.DestinationModified);
        RenameHint = $"Rename keeps both and saves the new file as '{Path.GetFileName(FileTransferPaths.GetUniquePath(conflict.DestinationPath))}'.";
        IsOpen = true;
    }

    private static string Describe(long size, DateTime modified, bool newer) =>
        string.Create(CultureInfo.CurrentCulture, $"{size:N0} bytes   {modified:g}{(newer ? "   (newer)" : string.Empty)}");

    private sealed class Request(FileConflict conflict)
    {
        public FileConflict Conflict { get; } = conflict;

        public TaskCompletionSource<FileConflictResolution> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationTokenRegistration Registration { get; set; }
    }
}

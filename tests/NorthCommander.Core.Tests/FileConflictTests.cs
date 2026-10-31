using NorthCommander.Core.Transfers;

namespace NorthCommander.Core.Tests;

public sealed class FileConflictTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("NorthCommanderConflict_");
    private readonly ScriptedResolver _resolver = new();
    private readonly BackgroundTransferQueue _queue;

    public FileConflictTests()
    {
        _queue = new BackgroundTransferQueue([new CopyFileOperation(), new MoveFileOperation()], conflictResolver: _resolver);
    }

    public void Dispose()
    {
        _queue.Dispose();
        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task Overwrite_replaces_existing_file()
    {
        var source = WriteFile("src/a.txt", 5);
        var destination = WriteFile("dst/a.txt", 9);
        _resolver.Answers.Enqueue(FileConflictResolution.Overwrite);

        var task = _queue.Enqueue(FileTransferType.Copy, source, destination);
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        Assert.Single(_resolver.Conflicts);
        Assert.Equal(9, _resolver.Conflicts[0].DestinationSize);
        Assert.Equal(5, _resolver.Conflicts[0].SourceSize);
        Assert.Equal(["a.txt"], Directory.GetFiles(PathOf("dst")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(FileTransferType.Copy)]
    [InlineData(FileTransferType.Move)]
    public async Task Skip_keeps_existing_file_and_source(FileTransferType type)
    {
        var source = WriteFile("src/a.txt", 5);
        var destination = WriteFile("dst/a.txt", 9);
        _resolver.Answers.Enqueue(FileConflictResolution.Skip);

        var task = _queue.Enqueue(type, source, destination);
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Equal(9, new FileInfo(destination).Length);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(FileTransferType.Copy)]
    [InlineData(FileTransferType.Move)]
    public async Task Rename_keeps_both_files(FileTransferType type)
    {
        var source = WriteFile("src/a.txt", 5);
        var destination = WriteFile("dst/a.txt", 9);
        WriteFile("dst/a (2).txt", 1);
        _resolver.Answers.Enqueue(FileConflictResolution.Rename);

        var task = _queue.Enqueue(type, source, destination);
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Equal(9, new FileInfo(destination).Length);
        Assert.Equal(5, new FileInfo(PathOf("dst/a (3).txt")).Length);
        Assert.Equal(type == FileTransferType.Copy, File.Exists(source));
    }

    [Fact]
    public async Task Overwrite_all_applies_to_the_whole_batch()
    {
        var batch = new FileTransferBatch();
        WriteFile("dst/a.txt", 9);
        WriteFile("dst/b.txt", 9);
        _resolver.Answers.Enqueue(FileConflictResolution.OverwriteAll);

        var first = _queue.Enqueue(new FileTransferTask(FileTransferType.Copy, WriteFile("src/a.txt", 5), PathOf("dst/a.txt"), batch));
        var second = _queue.Enqueue(new FileTransferTask(FileTransferType.Copy, WriteFile("src/b.txt", 6), PathOf("dst/b.txt"), batch));
        await WaitForAsync(first, FileTransferStatus.Completed);
        await WaitForAsync(second, FileTransferStatus.Completed);

        Assert.Single(_resolver.Conflicts);
        Assert.Equal(5, new FileInfo(PathOf("dst/a.txt")).Length);
        Assert.Equal(6, new FileInfo(PathOf("dst/b.txt")).Length);
    }

    [Fact]
    public async Task Other_batches_are_asked_again()
    {
        WriteFile("dst/a.txt", 9);
        WriteFile("dst/b.txt", 9);
        _resolver.Answers.Enqueue(FileConflictResolution.SkipAll);
        _resolver.Answers.Enqueue(FileConflictResolution.Overwrite);

        var first = _queue.Enqueue(FileTransferType.Copy, WriteFile("src/a.txt", 5), PathOf("dst/a.txt"));
        var second = _queue.Enqueue(FileTransferType.Copy, WriteFile("src/b.txt", 6), PathOf("dst/b.txt"));
        await WaitForAsync(second, FileTransferStatus.Completed);

        Assert.Equal(FileTransferStatus.Completed, first.Status);
        Assert.Equal(2, _resolver.Conflicts.Count);
        Assert.Equal(9, new FileInfo(PathOf("dst/a.txt")).Length);
        Assert.Equal(6, new FileInfo(PathOf("dst/b.txt")).Length);
    }

    [Fact]
    public async Task Skip_all_inside_directory_copy_asks_once_and_copies_the_rest()
    {
        WriteFile("src/dir/one.txt", 1);
        WriteFile("src/dir/two.txt", 2);
        WriteFile("src/dir/new.txt", 3);
        WriteFile("dst/dir/one.txt", 10);
        WriteFile("dst/dir/two.txt", 20);
        _resolver.Answers.Enqueue(FileConflictResolution.SkipAll);

        var task = _queue.Enqueue(FileTransferType.Copy, PathOf("src/dir"), PathOf("dst/dir"));
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Single(_resolver.Conflicts);
        Assert.Equal(10, new FileInfo(PathOf("dst/dir/one.txt")).Length);
        Assert.Equal(20, new FileInfo(PathOf("dst/dir/two.txt")).Length);
        Assert.Equal(3, new FileInfo(PathOf("dst/dir/new.txt")).Length);
        Assert.Equal(100, task.ProgressPercentage);
    }

    [Fact]
    public async Task Moving_directory_with_skipped_files_leaves_them_in_source()
    {
        WriteFile("src/dir/keep.txt", 1);
        WriteFile("src/dir/sub/moved.txt", 2);
        WriteFile("dst/dir/keep.txt", 10);
        _resolver.Answers.Enqueue(FileConflictResolution.Skip);

        var task = _queue.Enqueue(FileTransferType.Move, PathOf("src/dir"), PathOf("dst/dir"));
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.True(File.Exists(PathOf("src/dir/keep.txt")));
        Assert.False(Directory.Exists(PathOf("src/dir/sub")));
        Assert.Equal(2, new FileInfo(PathOf("dst/dir/sub/moved.txt")).Length);
        Assert.Equal(10, new FileInfo(PathOf("dst/dir/keep.txt")).Length);
    }

    [Fact]
    public async Task Cancel_answer_cancels_task_and_rest_of_batch()
    {
        var batch = new FileTransferBatch();
        WriteFile("dst/a.txt", 9);
        _resolver.Answers.Enqueue(FileConflictResolution.Cancel);

        var first = _queue.Enqueue(new FileTransferTask(FileTransferType.Copy, WriteFile("src/a.txt", 5), PathOf("dst/a.txt"), batch));
        var second = _queue.Enqueue(new FileTransferTask(FileTransferType.Copy, WriteFile("src/b.txt", 6), PathOf("dst/b.txt"), batch));
        await WaitForAsync(first, FileTransferStatus.Canceled);
        await WaitForAsync(second, FileTransferStatus.Canceled);

        Assert.True(batch.IsCanceled);
        Assert.Equal(9, new FileInfo(PathOf("dst/a.txt")).Length);
        Assert.False(File.Exists(PathOf("dst/b.txt")));
    }

    [Fact]
    public async Task Canceling_task_while_waiting_for_answer_abandons_the_question()
    {
        var source = WriteFile("src/a.txt", 5);
        var destination = WriteFile("dst/a.txt", 9);
        _resolver.Block = true;

        var task = _queue.Enqueue(FileTransferType.Copy, source, destination);
        await WaitUntilAsync(() => task.PendingConflict is not null);
        Assert.Equal(FileTransferStatus.Running, task.Status);

        _queue.Cancel(task);
        await WaitForAsync(task, FileTransferStatus.Canceled);

        Assert.Null(task.PendingConflict);
        Assert.Equal(9, new FileInfo(destination).Length);
    }

    [Fact]
    public void Unique_path_appends_counter()
    {
        WriteFile("a.tar.gz", 1);
        Assert.Equal(PathOf("a.tar (2).gz"), FileTransferPaths.GetUniquePath(PathOf("a.tar.gz")));
    }

    private string PathOf(string relative) => Path.Combine(_root.FullName, relative);

    private string WriteFile(string relative, int size)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static Task WaitForAsync(FileTransferTask task, FileTransferStatus status) =>
        WaitUntilAsync(() => task.Status == status);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ScriptedResolver : IFileConflictResolver
    {
        public Queue<FileConflictResolution> Answers { get; } = new();

        public List<FileConflict> Conflicts { get; } = [];

        public bool Block { get; set; }

        public async Task<FileConflictResolution> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
        {
            lock (Conflicts)
            {
                Conflicts.Add(conflict);
            }

            if (Block)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            }

            lock (Answers)
            {
                return Answers.Dequeue();
            }
        }
    }
}

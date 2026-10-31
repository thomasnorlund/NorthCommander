using CommanderTom.Core.Transfers;

namespace CommanderTom.Core.Tests;

public sealed class BackgroundTransferQueueTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("CommanderTomQueue_");
    private readonly BackgroundTransferQueue _queue = new([new CopyFileOperation(), new MoveFileOperation()]);

    public void Dispose()
    {
        _queue.Dispose();
        _root.Delete(recursive: true);
    }

    [Fact]
    public async Task Copies_file_and_preserves_timestamp()
    {
        var source = WriteFile("src/a.bin", 3 * 1024 * 1024 + 7);
        var modified = new DateTime(2023, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, modified);
        var destination = PathOf("dst/a.bin");

        var task = _queue.Enqueue(FileTransferType.Copy, source, destination);
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(destination));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(destination));
        Assert.Equal(100, task.ProgressPercentage);
        Assert.Equal(task.TotalBytes, task.TransferredBytes);
        Assert.Empty(_queue.Tasks);
    }

    [Fact]
    public async Task Copies_directory_tree()
    {
        WriteFile("src/dir/one.txt", 10);
        WriteFile("src/dir/sub/two.txt", 20);
        Directory.CreateDirectory(PathOf("src/dir/empty"));

        var task = _queue.Enqueue(FileTransferType.Copy, PathOf("src/dir"), PathOf("dst/dir"));
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.Equal(10, new FileInfo(PathOf("dst/dir/one.txt")).Length);
        Assert.Equal(20, new FileInfo(PathOf("dst/dir/sub/two.txt")).Length);
        Assert.True(Directory.Exists(PathOf("dst/dir/empty")));
        Assert.Equal(30, task.TotalBytes);
    }

    [Fact]
    public async Task Moves_directory()
    {
        WriteFile("src/dir/one.txt", 10);

        var task = _queue.Enqueue(FileTransferType.Move, PathOf("src/dir"), PathOf("dst/dir"));
        await WaitForAsync(task, FileTransferStatus.Completed);

        Assert.False(Directory.Exists(PathOf("src/dir")));
        Assert.True(File.Exists(PathOf("dst/dir/one.txt")));
    }

    [Fact]
    public async Task Fails_without_overwriting_existing_file()
    {
        var source = WriteFile("src/a.txt", 5);
        var destination = WriteFile("dst/a.txt", 9);

        var task = _queue.Enqueue(FileTransferType.Copy, source, destination);
        await WaitForAsync(task, FileTransferStatus.Failed);

        Assert.NotNull(task.ErrorMessage);
        Assert.Equal(9, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task Fails_when_copying_folder_into_itself()
    {
        WriteFile("src/dir/a.txt", 1);

        var task = _queue.Enqueue(FileTransferType.Copy, PathOf("src/dir"), PathOf("src/dir/inner"));
        await WaitForAsync(task, FileTransferStatus.Failed);

        Assert.False(Directory.Exists(PathOf("src/dir/inner")));
    }

    [Fact]
    public async Task Processes_tasks_sequentially_in_fifo_order()
    {
        var completed = new List<string>();
        _queue.TaskChanged += (_, t) =>
        {
            if (t.Status == FileTransferStatus.Completed)
            {
                lock (completed)
                {
                    completed.Add(t.DisplayName);
                }
            }
        };

        var tasks = Enumerable.Range(0, 5)
            .Select(i => _queue.Enqueue(FileTransferType.Copy, WriteFile($"src/{i}.bin", 64 * 1024), PathOf($"dst/{i}.bin")))
            .ToList();
        foreach (var task in tasks)
        {
            await WaitForAsync(task, FileTransferStatus.Completed);
        }

        lock (completed)
        {
            Assert.Equal(["0.bin", "1.bin", "2.bin", "3.bin", "4.bin"], completed);
        }
    }

    [Fact]
    public async Task Pause_and_resume_running_task()
    {
        var gate = new TaskCompletionSource();
        var queue = new BackgroundTransferQueue([new BlockingOperation(gate.Task)]);
        try
        {
            var task = queue.Enqueue(FileTransferType.Copy, PathOf("x"), PathOf("y"));
            await WaitForAsync(task, FileTransferStatus.Running);

            Assert.True(queue.Pause(task));
            Assert.Equal(FileTransferStatus.Paused, task.Status);
            gate.SetResult();
            await Task.Delay(200);
            Assert.Equal(FileTransferStatus.Paused, task.Status);

            Assert.True(queue.Resume(task));
            await WaitForAsync(task, FileTransferStatus.Completed);
        }
        finally
        {
            queue.Dispose();
        }
    }

    [Fact]
    public async Task Paused_queued_task_is_skipped_until_resumed()
    {
        var gate = new TaskCompletionSource();
        var queue = new BackgroundTransferQueue([new BlockingOperation(gate.Task)]);
        try
        {
            var first = queue.Enqueue(FileTransferType.Copy, PathOf("a"), PathOf("b"));
            var second = queue.Enqueue(FileTransferType.Copy, PathOf("c"), PathOf("d"));
            var third = queue.Enqueue(FileTransferType.Copy, PathOf("e"), PathOf("f"));
            Assert.True(queue.Pause(second));

            gate.SetResult();
            await WaitForAsync(first, FileTransferStatus.Completed);
            await WaitForAsync(third, FileTransferStatus.Completed);
            Assert.Equal(FileTransferStatus.Paused, second.Status);

            Assert.True(queue.Resume(second));
            await WaitForAsync(second, FileTransferStatus.Completed);
        }
        finally
        {
            queue.Dispose();
        }
    }

    [Fact]
    public async Task Cancel_running_task_removes_partial_file()
    {
        var source = WriteFile("src/big.bin", 8 * 1024 * 1024);
        var destination = PathOf("dst/big.bin");

        var task = _queue.Enqueue(FileTransferType.Copy, source, destination);
        _queue.Pause(task);
        Assert.True(_queue.Cancel(task));
        await WaitForAsync(task, FileTransferStatus.Canceled);

        var running = _queue.Enqueue(FileTransferType.Copy, source, destination);
        await WaitUntilAsync(() => running.TransferredBytes > 0 || running.IsFinished);
        _queue.Pause(running);
        await Task.Delay(100);
        _queue.Cancel(running);
        await WaitForAsync(running, FileTransferStatus.Canceled, FileTransferStatus.Completed);

        if (running.Status == FileTransferStatus.Canceled)
        {
            Assert.False(File.Exists(destination));
        }
    }

    [Fact]
    public async Task Cancel_queued_task_never_runs()
    {
        var gate = new TaskCompletionSource();
        var operation = new BlockingOperation(gate.Task);
        var queue = new BackgroundTransferQueue([operation]);
        try
        {
            var first = queue.Enqueue(FileTransferType.Copy, PathOf("a"), PathOf("b"));
            var second = queue.Enqueue(FileTransferType.Copy, PathOf("c"), PathOf("d"));

            Assert.True(queue.Cancel(second));
            Assert.Equal(FileTransferStatus.Canceled, second.Status);

            gate.SetResult();
            await WaitForAsync(first, FileTransferStatus.Completed);
            await Task.Delay(100);
            Assert.Equal(1, operation.ExecutionCount);
        }
        finally
        {
            queue.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolves_destination_inside_existing_directory(bool multiple)
    {
        Directory.CreateDirectory(PathOf("target"));

        var destination = FileTransferPaths.ResolveDestination(PathOf("src/a.txt"), PathOf("target"), multiple);

        Assert.Equal(PathOf("target/a.txt"), destination);
    }

    [Fact]
    public void Resolves_destination_as_new_name_for_single_item()
    {
        var destination = FileTransferPaths.ResolveDestination(PathOf("src/a.txt"), PathOf("renamed.txt"), multipleSources: false);

        Assert.Equal(PathOf("renamed.txt"), destination);
    }

    [Fact]
    public void Validate_rejects_same_path()
    {
        var source = WriteFile("a.txt", 1);

        Assert.NotNull(FileTransferPaths.Validate(source, source));
    }

    private string PathOf(string relative) =>
        Path.Combine(_root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    private string WriteFile(string relative, int size)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Task WaitForAsync(FileTransferTask task, params FileTransferStatus[] statuses) =>
        WaitUntilAsync(() => statuses.Contains(task.Status));

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

    private sealed class BlockingOperation(Task gate) : IFileOperation
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public FileTransferType Type => FileTransferType.Copy;

        public async Task ExecuteAsync(FileTransferTask task, FileOperationContext context)
        {
            Interlocked.Increment(ref _executionCount);
            await gate.WaitAsync(context.CancellationToken);
            await context.CheckpointAsync();
        }
    }
}

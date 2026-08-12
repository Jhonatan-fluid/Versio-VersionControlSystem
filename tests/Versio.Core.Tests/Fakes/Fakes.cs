using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Plc;
using Versio.Core.Plc.Codesys;
using Versio.Core.Locking;

namespace Versio.Core.Tests.Fakes;

/// <summary>In-memory filesystem. Paths are compared case-insensitively, like Windows.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make the next copy fail, standing in for a share-locked project.</summary>
    public int FailCopyTimes { get; set; }

    public void Write(string path, string content)
    {
        _files[path] = Encoding.UTF8.GetBytes(content);

        // Every ancestor, not just the immediate parent. A real filesystem cannot hold
        // "a/b/c.st" without "a/b" existing, and a fake that can lets a test pass against a
        // directory layout that could never occur.
        for (var dir = Path.GetDirectoryName(path);
             !string.IsNullOrEmpty(dir);
             dir = Path.GetDirectoryName(dir))
        {
            _dirs.Add(dir);
        }
    }

    public bool FileExists(string path) => _files.ContainsKey(path);

    public bool DirectoryExists(string path) => _dirs.Contains(path);

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path, out var b) ? b : []);

    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) =>
        Task.FromResult(_files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : "");

    public Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        Write(path, content);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Files beneath a directory — <b>respecting the directory boundary</b>.
    /// </summary>
    /// <remarks>
    /// This was a bare prefix match, which is not what a directory is. With an export tree at
    /// <c>plc/A</c> and its project at <c>plc/A.project</c>, the project file matched the
    /// prefix and was enumerated as part of its own export tree — so the tree hash moved
    /// whenever the project did, and a test written against it would have proven the opposite
    /// of what it claimed.
    /// </remarks>
    public IEnumerable<string> EnumerateFiles(string root, string searchPattern = "*")
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;

        return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        if (FailCopyTimes > 0)
        {
            FailCopyTimes--;
            throw new IOException("the file is in use by another process");
        }

        _files[destination] = _files.TryGetValue(source, out var b) ? b : [];
        return Task.CompletedTask;
    }

    public void DeleteFile(string path) => _files.Remove(path);

    public void CreateDirectory(string path) => _dirs.Add(path);
}

/// <summary>Lock store backed by a field, so a "crash" is just dropping the orchestrator.</summary>
public sealed class InMemoryLockStore : ILockStore
{
    public LockState? State { get; set; }
    public int Writes { get; private set; }

    public Task<LockState?> ReadAsync(CancellationToken ct) => Task.FromResult(State);

    public Task WriteAsync(LockState state, CancellationToken ct)
    {
        State = state;
        Writes++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct)
    {
        State = null;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Scripted CODESYS. Records every job and replays a queued result, so a failing export or
/// a timeout is a one-line setup rather than a real 20-second process.
/// </summary>
public sealed class FakeCodesysRunner : IPlcRunner
{
    private readonly Queue<PlcResult> _queued = new();

    public List<PlcJob> Jobs { get; } = [];

    public Func<PlcJob, Task>? BeforeRun { get; set; }

    public void EnqueueSuccess(params string[] changedPaths)
    {
        _queued.Enqueue(new PlcResult
        {
            Succeeded = true,
            Duration = TimeSpan.FromSeconds(18),
            ChangedPaths = changedPaths,
        });
    }

    public void EnqueueFailure(string message = "boom")
    {
        _queued.Enqueue(new PlcResult
        {
            Succeeded = false,
            FailureReason = message,
            Duration = TimeSpan.FromSeconds(2),
        });
    }

    public void EnqueueTimeout()
    {
        _queued.Enqueue(new PlcResult
        {
            Succeeded = false,
            FailureReason = "the toolchain did not finish within the timeout",
            Duration = TimeSpan.FromMinutes(10),
        });
    }

    public async Task<PlcResult> RunAsync(
        PlcJob job, IProgress<PlcProgress>? progress, CancellationToken ct)
    {
        Jobs.Add(job);
        if (BeforeRun is not null)
        {
            await BeforeRun(job).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        if (_queued.Count == 0)
        {
            EnqueueSuccess();
        }

        return _queued.Dequeue();
    }
}

public sealed class FakeProcessProbe(bool alive) : IProcessProbe
{
    public bool IsVersioProcessAlive(int processId, string sessionId) => alive;

    public bool IsProcessAlive(int processId) => alive;
}

public sealed class FakeMachineIdentity(DateTimeOffset boot, int pid = 4242) : IMachineIdentity
{
    public string MachineName => "TEST-PC";
    public string UserName => "tester";
    public int CurrentProcessId => pid;
    public DateTimeOffset BootId => boot;
}

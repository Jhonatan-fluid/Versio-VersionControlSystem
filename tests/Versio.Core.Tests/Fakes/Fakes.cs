using System.Text;
using Versio.Core.Abstractions;
using Versio.Core.Codesys;
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
        _dirs.Add(Path.GetDirectoryName(path) ?? string.Empty);
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

    public IEnumerable<string> EnumerateFiles(string root, string searchPattern = "*") =>
        _files.Keys.Where(k => k.StartsWith(root, StringComparison.OrdinalIgnoreCase));

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
public sealed class FakeCodesysRunner : ICodesysRunner
{
    private readonly Queue<CodesysResult> _queued = new();

    public List<CodesysJob> Jobs { get; } = [];

    public Func<CodesysJob, Task>? BeforeRun { get; set; }

    public void EnqueueSuccess(params string[] changedPaths)
    {
        var sb = new StringBuilder();
        foreach (var path in changedPaths)
        {
            sb.AppendLine($"@@VERSIO@@{{\"t\":\"object\",\"path\":\"{path}\"}}");
        }

        sb.AppendLine("@@VERSIO@@{\"t\":\"summary\",\"written\":" + changedPaths.Length + "}");
        sb.AppendLine("@@VERSIO@@{\"t\":\"done\"}");
        sb.AppendLine("@@VERSIO@@{\"t\":\"exit\",\"rc\":0}");

        _queued.Enqueue(new CodesysResult
        {
            ExitCode = 0,
            TimedOut = false,
            Duration = TimeSpan.FromSeconds(18),
            Output = ScriptProtocol.Parse(sb.ToString()),
        });
    }

    public void EnqueueFailure(string message = "boom")
    {
        _queued.Enqueue(new CodesysResult
        {
            ExitCode = 1,
            TimedOut = false,
            Duration = TimeSpan.FromSeconds(2),
            Output = ScriptProtocol.Parse(
                $"@@VERSIO@@{{\"t\":\"fatal\",\"msg\":\"{message}\"}}\n" +
                "@@VERSIO@@{\"t\":\"exit\",\"rc\":1}"),
        });
    }

    public void EnqueueTimeout()
    {
        _queued.Enqueue(new CodesysResult
        {
            ExitCode = -1,
            TimedOut = true,
            Duration = TimeSpan.FromMinutes(10),
            Output = ScriptProtocol.Parse(""),
        });
    }

    public async Task<CodesysResult> RunAsync(
        CodesysJob job, IProgress<CodesysProgress>? progress, CancellationToken ct)
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
}

public sealed class FakeMachineIdentity(DateTimeOffset boot, int pid = 4242) : IMachineIdentity
{
    public string MachineName => "TEST-PC";
    public string UserName => "tester";
    public int CurrentProcessId => pid;
    public DateTimeOffset BootId => boot;
}

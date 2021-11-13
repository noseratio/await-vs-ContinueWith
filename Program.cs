namespace Test;

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async Task<DBNull> DoSomething()
    {
        await Task.Yield();
        return DBNull.Value; 
    }

    static async Task<DBNull?> TestWithAsyncStateMachine()
    {
        try
        {
            return await DoSomething();
        }
        catch
        {
            return null;
        }
    }

    static Task<DBNull?> TestWithoutAsyncStateMachine()
    {
        return DoSomething().ContinueWith(
            t => t.IsCompletedSuccessfully ? t.GetAwaiter().GetResult() : null,
            cancellationToken: default,
            continuationOptions: TaskContinuationOptions.ExecuteSynchronously,
            scheduler: TaskScheduler.Default);
    }

    static async Task<long> Test(Func<Task<DBNull?>> tester, int count)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            await tester();
        }
        return sw.ElapsedMilliseconds;
    }

    static async Task RunTest(
        Func<Func<Task<long>>, Task<long>> runner,
        int count)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var withoutAsync = await runner(() => Test(TestWithoutAsyncStateMachine, count));
        Console.WriteLine($"{nameof(TestWithoutAsyncStateMachine)}: {withoutAsync}ms");

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
        var withAsync = await runner(() => Test(TestWithAsyncStateMachine, count));
        Console.WriteLine($"{nameof(TestWithAsyncStateMachine)}: {withAsync}ms");
    }

    static async Task Main()
    {
        const int threads = 2000;
        const int iterations = 25000000;

        ThreadPool.SetMinThreads(threads, threads);
        ThreadPool.SetMaxThreads(threads, threads);

        var pump = new PumpingSyncContext();

        Console.WriteLine($"Testing with {nameof(PumpingSyncContext)}...");
        await RunTest(func => pump.Run(func), iterations);

        Console.WriteLine($"Testing with {nameof(Task.Run)}...");
        await RunTest(func => Task.Run(func), iterations);

        await pump.Complete();
        Console.WriteLine("Ended");
    }
}

/// <summary>
/// Test async calls on single thread for more deterministic results
/// </summary>
public class PumpingSyncContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _workItems = new();
    private readonly Task _threadTask;
    private readonly TaskScheduler _taskScheduler;

    public PumpingSyncContext()
    {
        var tcs = new TaskCompletionSource<TaskScheduler>();
        _threadTask = Task.Factory.StartNew(() =>
        {
            SetSynchronizationContext(this);
            try
            {
                tcs.SetResult(TaskScheduler.FromCurrentSynchronizationContext());
                foreach (var (callback, arg) in _workItems.GetConsumingEnumerable())
                    callback(arg);
            }
            finally
            {
                SetSynchronizationContext(null);
            }
        }, TaskCreationOptions.LongRunning);
        _taskScheduler = tcs.Task.GetAwaiter().GetResult();
    }

    public Task Complete()
    {
        _workItems.CompleteAdding();
        return _threadTask;
    }

    public override void Post(SendOrPostCallback d, object? state) =>
        _workItems.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotImplementedException(nameof(Send));

    public override SynchronizationContext CreateCopy() => this;

    public Task<T> Run<T>(Func<Task<T>> func, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(func, cancellationToken, creationOptions: default, _taskScheduler).Unwrap();
}

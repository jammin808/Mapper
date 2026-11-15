using System.Globalization;
using System.Runtime.ExceptionServices;

namespace PlayMap.Tests;

/// <summary>A test's piece of work on a background thread of its own, never a thread-pool worker: a deadline the test puts on the
/// work's start or its end counts the work alone, so a pool kept busy by other tests cannot fail it, while work that runs past a
/// bound that is the test's point still does. What the work threw is thrown on the test's thread when it is joined; a thread still
/// running past its bound is left to end with the test process, as a managed thread cannot be stopped safely. Only the work's own
/// thread is its own: an async delegate started here runs on it up to its first await that has not finished, and continues where
/// that await continues (for a plain await, on the pool).</summary>
internal sealed class OwnThread
{
    private readonly Thread _thread;
    private readonly string _what;
    private ExceptionDispatchInfo? _fault;

    private OwnThread(string what, Action work)
    {
        _what = what;
        _thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                Volatile.Write(ref _fault, ExceptionDispatchInfo.Capture(e));
            }
        })
        { IsBackground = true, Name = what };
        _thread.Start();
    }

    /// <summary>Starts <paramref name="work"/> on a thread of its own named <paramref name="what"/>, for the test to join.</summary>
    public static OwnThread Start(string what, Action work) => new(what, work);

    /// <summary>Does <paramref name="work"/> on a thread of its own and joins it within <paramref name="bound"/>.</summary>
    public static void Run(string what, TimeSpan bound, Action work) => Start(what, work).Join(bound);

    /// <summary>Answers what <paramref name="work"/> answered, done on a thread of its own and joined within <paramref name="bound"/>.</summary>
    public static T Run<T>(string what, TimeSpan bound, Func<T> work)
    {
        var result = default(T);
        Start(what, () => result = work()).Join(bound);
        return result!;
    }

    /// <summary>Starts an async <paramref name="work"/> on a thread of its own and hands back its task, for the test to wait on under
    /// its own deadline: the delegate's start never waits for a pool worker; its awaits continue where they continue. What it threw
    /// before it handed back its task is the task's fault.</summary>
    public static Task StartAsync(string what, Func<Task> work)
    {
        // Completed on the work's own thread, never queued to the pool: the handed-back task follows the delegate's at once.
        var started = new TaskCompletionSource<Task>();
        Start(what, () =>
        {
            try
            {
                started.SetResult(work());
            }
            catch (Exception e)
            {
                started.SetException(e);
            }
        });
        return started.Task.Unwrap();
    }

    /// <summary>The work has not ended.</summary>
    public bool Running => _thread.IsAlive;

    /// <summary>Waits for the work to end within <paramref name="bound"/>, failing with words that name the bound if it has not, and
    /// throws on the test's thread what the work threw.</summary>
    public void Join(TimeSpan bound)
    {
        Xunit.Assert.True(_thread.Join(bound), string.Create(CultureInfo.InvariantCulture,
            $"the {_what} ran past its bound of {bound.TotalSeconds:0.###} s"));
        Volatile.Read(ref _fault)?.Throw();
    }
}

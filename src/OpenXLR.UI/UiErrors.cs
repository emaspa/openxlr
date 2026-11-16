using System;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace OpenXLR.UI;

/// <summary>
/// The last line under the UI's own code: an exception nothing caught on the
/// UI thread, an <c>async void</c> handler's included, and a faulted task
/// nobody awaited are written to standard error, which the journal keeps,
/// and the UI goes on. A daemon state that cannot be applied is caught
/// earlier, in <see cref="MainViewModel"/>, where the version banner says so.
/// </summary>
internal static class UiErrors
{
    /// <summary>Where a caught exception is written; tests replace it.</summary>
    internal static Action<string> Write { get; set; } = line => Console.Error.WriteLine(line);

    /// <summary>Write what failed, with the exception's type, message and stack.</summary>
    internal static void Log(string what, Exception ex) => Write($"OpenXLR UI: {what}: {ex}");

    /// <summary>Hook the dispatcher and the task scheduler once, after the framework is up.</summary>
    internal static void Install()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log("unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }
}

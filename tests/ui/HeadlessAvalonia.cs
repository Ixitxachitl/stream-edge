// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Headless;
using StreamEdge.AvaloniaApp;
using Xunit;

namespace StreamEdge.UiTests;

/// <summary>One Avalonia UI thread, shared by every test in the assembly.
///
/// Avalonia binds its dispatcher to whichever thread set the platform up, and
/// controls may only be built and drawn on that thread, but xunit hands each
/// test whatever pool thread is free. So the fixture owns a thread, starts the
/// real App on it once, and marshals each test body across.</summary>
public sealed class HeadlessAvalonia : IDisposable
{
    public const string CollectionName = "avalonia";
    private readonly BlockingCollection<Action> _work = new();

    public HeadlessAvalonia()
    {
        // Keep tests away from the user's real settings and secrets.
        AppData.Override = Path.Combine(Path.GetTempPath(), "StreamEdge.UiTests." + Guid.NewGuid().ToString("N"));

        using var ready = new ManualResetEventSlim();
        ExceptionDispatchInfo? startupFailure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Skia rather than the headless stub renderer: the point is to
                // render real pixels.
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .WithInterFont()
                    .SetupWithoutStarting();
            }
            catch (Exception ex)
            {
                startupFailure = ExceptionDispatchInfo.Capture(ex);
                return;
            }
            finally
            {
                ready.Set();
            }
            foreach (var job in _work.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true,
            Name = "Avalonia UI (tests)",
        };
        thread.Start();
        ready.Wait();
        startupFailure?.Throw();
    }

    /// <summary>Runs a test body on the UI thread and brings any failure back
    /// with its stack intact.</summary>
    public void Run(Action body)
    {
        using var done = new ManualResetEventSlim();
        ExceptionDispatchInfo? failure = null;
        _work.Add(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });
        done.Wait();
        failure?.Throw();
    }

    public void Dispose() => _work.CompleteAdding();
}

[CollectionDefinition(HeadlessAvalonia.CollectionName)]
public sealed class HeadlessAvaloniaCollection : ICollectionFixture<HeadlessAvalonia>;

[Collection(HeadlessAvalonia.CollectionName)]
public abstract class UiTest(HeadlessAvalonia ui)
{
    protected void Ui(Action body) => ui.Run(body);
}

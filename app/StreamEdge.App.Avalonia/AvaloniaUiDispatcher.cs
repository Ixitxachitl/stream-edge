// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Threading;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

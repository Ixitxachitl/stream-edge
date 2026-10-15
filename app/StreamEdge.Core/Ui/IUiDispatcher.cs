// SPDX-License-Identifier: GPL-3.0-or-later
namespace StreamEdge.Ui;

/// <summary>Marshals work onto the UI thread without Core depending on a UI
/// framework. The Avalonia app supplies the real one; tests run inline.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class InlineDispatcher : IUiDispatcher
{
    public static readonly InlineDispatcher Instance = new();
    public void Post(Action action) => action();
}

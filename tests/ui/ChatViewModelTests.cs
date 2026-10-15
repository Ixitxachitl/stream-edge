// SPDX-License-Identifier: GPL-3.0-or-later
using StreamEdge.AvaloniaApp.ViewModels;
using Xunit;

namespace StreamEdge.UiTests;

public class ChatViewModelTests
{
    [Fact]
    public void PlaceholderFollowsWhetherSendingIsPossible()
    {
        var chat = new ChatViewModel(new AppSettings());
        var raised = new List<string?>();
        chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.Equal("Log in to chat", chat.Placeholder);
        chat.CanSend = true;
        Assert.Equal("Send a message", chat.Placeholder);
        Assert.Contains(nameof(ChatViewModel.Placeholder), raised);
    }
}

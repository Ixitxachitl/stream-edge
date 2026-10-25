// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamEdge.Chat;
using StreamEdge.Streams;
using StreamEdge.Ui;

namespace StreamEdge.AvaloniaApp.ViewModels;

/// <summary>The ✉ inbox: whispers kept out of the chats, one conversation
/// per person, most recent first, saved between runs (Twitch has no history
/// to fetch them from).</summary>
public sealed partial class WhisperInboxViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ChatServices? _services;
    // To whom (login, and user id when known), what: why not, or null once sent.
    private readonly Func<string, string?, string, Task<string?>> _send;
    private WhisperArchive _archive = new();

    /// <param name="services">For emotes; null draws plain text.</param>
    /// <param name="local">Channels the app knows of, for <see cref="Recipients"/>.</param>
    /// <param name="remote">Twitch's channel search, for <see cref="Recipients"/>.</param>
    public WhisperInboxViewModel(AppSettings settings, ChatServices? services, Func<string, string?, string, Task<string?>> send,
        Func<string, IEnumerable<ChannelSuggestion>> local,
        Func<string, CancellationToken, Task<IReadOnlyList<ChannelSuggestion>>> remote, IUiDispatcher ui)
    {
        _settings = settings;
        _services = services;
        _send = send;
        // Searched as the live list's box is, people already whispered first.
        Recipients = new ChannelSearchViewModel(q => Known(q).Concat(local(q)), remote, (_, _) => RecipientPicked?.Invoke(), ui)
        {
            KeepsPick = true,
            TypedDetail = "Whisper this name",
        };
        Recipients.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChannelSearchViewModel.Query)) SendCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Emotes and spelling for the message box; null in tests.</summary>
    public ChatServices? Services => _services;

    /// <summary>Someone in the inbox, by login or name: not a misspelling.</summary>
    public bool IsName(string word) =>
        Threads.Any(t => string.Equals(t.Login, word, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(t.Name, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>Who a new whisper goes to: typed, or picked from suggestions.</summary>
    public ChannelSearchViewModel Recipients { get; }

    /// <summary>Someone was picked in <see cref="Recipients"/>: on to the message.</summary>
    public event Action? RecipientPicked;

    private IEnumerable<ChannelSuggestion> Known(string query) =>
        Threads.Where(t => ChannelSearch.Matches(t.Login, t.Name, query)).Select(t => new ChannelSuggestion(t.Login, t.Name));

    public ObservableCollection<WhisperThreadViewModel> Threads { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private WhisperThreadViewModel? _selected;

    /// <summary>Writing to someone new, named in <see cref="Recipients"/>, rather
    /// than answering <see cref="Selected"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(Placeholder))]
    private bool _composing;

    /// <summary>The whisper being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = "";

    /// <summary>Why the last whisper didn't go.</summary>
    [ObservableProperty]
    private string? _status;

    public string Placeholder => Composing ? "Message" : "Whisper back";

    /// <summary>Their whispers not yet seen, over all conversations.</summary>
    public int Unread => Threads.Sum(t => t.Unread);

    public bool HasUnread => Unread > 0;
    public bool HasThreads => Threads.Count > 0;

    /// <summary>Showing on screen (set by the view): what arrives in the
    /// open conversation is seen already. Empty, it opens ready to write.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            _isOpen = value;
            if (!value)
            {
                Recipients.Close();
                return;
            }
            MarkRead(Selected);
            if (Selected is null) Composing = true;
        }
    }

    private bool _isOpen;

    /// <summary>Asks the view to show the inbox.</summary>
    public event Action? OpenRequested;

    /// <summary>The saved inbox of this account (none signed out).</summary>
    public void Load(string? owner)
    {
        _archive = _settings.ReadOnly ? new() : WhisperArchive.Load(owner);
        Threads.Clear();
        foreach (var c in _archive.Conversations) Threads.Add(new WhisperThreadViewModel(c, Entry));
        Selected = Threads.FirstOrDefault();
        Composing = false;
        Changed();
    }

    /// <summary>Shows the inbox, at the conversation with <paramref name="login"/>.</summary>
    public void Open(string? login = null)
    {
        if (Find(login) is { } thread) Selected = thread;
        OpenRequested?.Invoke();
    }

    /// <summary>Start a whisper to someone not in the list (or anyone).</summary>
    [RelayCommand]
    private void Compose()
    {
        Selected = null;
        Status = null;
        Recipients.Cancel();
        Composing = true;
    }

    /// <summary>A whisper to the user. Returns whether it's in plain view
    /// (no alert needed).</summary>
    public bool Received(string login, string name, string? userId, string text, DateTimeOffset time)
    {
        var thread = Add(login, name, userId, new WhisperLine(time, false, text));
        var seen = IsOpen && Selected == thread;
        if (!seen) thread.Unread++;
        if (Selected is null && !Composing) Selected = thread;
        Changed();
        return seen;
    }

    /// <summary>A whisper the user sent, from here or with /w.</summary>
    public void Sent(string login, string name, string? userId, string text, DateTimeOffset time)
    {
        Add(login, name, userId, new WhisperLine(time, true, text));
        Changed();
    }

    /// <summary>Emotes or GIF settings changed: draw every line again.</summary>
    public void Render()
    {
        foreach (var thread in Threads)
            foreach (var entry in thread.Lines)
                entry.Tokens = Tokenize(entry.Message);
    }

    private WhisperThreadViewModel? Find(string? login) =>
        login is null ? null : Threads.FirstOrDefault(t => string.Equals(t.Login, login, StringComparison.OrdinalIgnoreCase));

    /// <summary>The line added to its conversation, which moves to the top.</summary>
    private WhisperThreadViewModel Add(string login, string name, string? userId, WhisperLine line)
    {
        var thread = Find(login);
        if (thread is null)
        {
            thread = new WhisperThreadViewModel(new WhisperConversation { Login = login.ToLowerInvariant() }, Entry);
            _archive.Conversations.Insert(0, thread.Model);
            Threads.Insert(0, thread);
        }
        else
        {
            _archive.Conversations.Remove(thread.Model);
            _archive.Conversations.Insert(0, thread.Model);
            var at = Threads.IndexOf(thread);
            if (at > 0)
            {
                // The list drops its selection over a move; keep it.
                var selected = Selected;
                Threads.Move(at, 0);
                Selected = selected;
            }
        }
        thread.Rename(name, userId);
        thread.Add(line);
        return thread;
    }

    /// <summary>A whisper as a chat line, so it's drawn like one: emotes,
    /// links and mentions. Whispers carry no emote positions, so Twitch
    /// emotes are matched by code against the user's own, as for lines they
    /// send in chat; their names aren't links (a user card can't open over
    /// the inbox).</summary>
    private ChatEntry Entry(WhisperConversation with, WhisperLine line)
    {
        var name = line.FromMe ? "You" : with.Name.Length > 0 ? with.Name : with.Login;
        var color = line.FromMe ? "#A970FF" : UserColors.For(with.Login, null);
        var message = new ChatMessage(line.Time, ChatMessageKind.Message, "", null, name, color, [], line.Text);
        return new ChatEntry(message, TimeOf(line.Time)) { IsOwn = line.FromMe, Tokens = Tokenize(message) };
    }

    private IReadOnlyList<ChatToken> Tokenize(ChatMessage message) => _services is { } s
        ? ChatTokenizer.Tokenize(message, code => s.SendableEmote(code, null), s.SelfLogin(), _settings.ShowGifs)
        : ChatTokenizer.Tokenize(message, null, null);

    partial void OnSelectedChanged(WhisperThreadViewModel? value)
    {
        Status = null;
        if (value is null) return;
        Composing = false;
        Recipients.Close();
        if (IsOpen) MarkRead(value);
    }

    private void MarkRead(WhisperThreadViewModel? thread)
    {
        if (thread is not { Unread: > 0 }) return;
        thread.Unread = 0;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Unread));
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(HasThreads));
        if (_settings.ReadOnly) return;
        try
        {
            _archive.Save();
        }
        catch (IOException)
        {
            // Kept in memory; the next whisper tries again.
        }
    }

    /// <summary>Who's being written to.</summary>
    private string? Recipient => Composing
        ? ChannelSearch.Normalize(Recipients.Query) is { Length: > 0 } login ? login : null
        : Selected?.Login;

    private bool CanSend() => Recipient is not null && !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (Recipient is not { } login) return;
        var composing = Composing;
        var text = Draft.Trim();
        Draft = "";
        Status = await _send(login, composing ? null : Selected?.UserId, text);
        if (Status is not null)
        {
            // Not sent: back in the box to try again.
            if (Draft.Length == 0) Draft = text;
            return;
        }
        // A new conversation, or one already in the list: open it.
        if (composing && Find(login) is { } thread)
        {
            Selected = thread;
            Recipients.Cancel();
        }
    }

    /// <summary>The chat's timestamp format, with the date for other days.</summary>
    private string TimeOf(DateTimeOffset at)
    {
        var format = _settings.TimestampFormat.Length > 0 ? _settings.TimestampFormat : "H:mm";
        return at.Date == DateTimeOffset.Now.Date ? at.ToString(format) : at.ToString("MMM d ") + at.ToString(format);
    }
}

/// <summary>The conversation with one person.</summary>
public sealed partial class WhisperThreadViewModel : ObservableObject
{
    private readonly Func<WhisperConversation, WhisperLine, ChatEntry> _entry;

    public WhisperThreadViewModel(WhisperConversation model, Func<WhisperConversation, WhisperLine, ChatEntry> entry)
    {
        Model = model;
        _entry = entry;
        foreach (var line in model.Lines) Lines.Add(entry(model, line));
    }

    internal WhisperConversation Model { get; }

    public string Login => Model.Login;
    public string Name => Model.Name.Length > 0 ? Model.Name : Model.Login;
    public string? UserId => Model.UserId;

    /// <summary>The whispers, drawn as chat lines.</summary>
    public ObservableCollection<ChatEntry> Lines { get; } = [];

    public int Unread
    {
        get => Model.Unread;
        set
        {
            if (Model.Unread == value) return;
            Model.Unread = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnread));
        }
    }

    public bool HasUnread => Unread > 0;

    /// <summary>The last line, for the list.</summary>
    public string Preview => Model.Lines.Count == 0 ? ""
        : (Model.Lines[^1].FromMe ? "You: " : "") + Model.Lines[^1].Text;

    internal void Rename(string name, string? userId)
    {
        if (userId is not null) Model.UserId = userId;
        if (name.Length == 0 || name == Model.Name) return;
        Model.Name = name;
        OnPropertyChanged(nameof(Name));
        // Their lines carry the name: draw them again with the new one.
        var skip = Model.Lines.Count - Lines.Count;
        for (var i = 0; i < Lines.Count; i++)
            if (!Model.Lines[skip + i].FromMe) Lines[i] = _entry(Model, Model.Lines[skip + i]);
    }

    internal void Add(WhisperLine line)
    {
        Model.Lines.Add(line);
        Lines.Add(_entry(Model, line));
        while (Lines.Count > WhisperArchive.MaxLines) Lines.RemoveAt(0);
        OnPropertyChanged(nameof(Preview));
    }
}

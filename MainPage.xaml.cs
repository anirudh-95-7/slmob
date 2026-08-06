using System.Collections.ObjectModel;
using LibreMetaverse;
using SLMobileViewer.Services;

namespace SLMobileViewer;

public partial class MainPage : ContentPage
{
    private readonly SecondLifeService _sl = SecondLifeService.Instance;

    private readonly ObservableCollection<string> _chatLines = new();
    private readonly ObservableCollection<NearbyPrimVm> _nearby = new();
    private readonly ObservableCollection<PersonVm> _people = new();
    private readonly ObservableCollection<FriendVm> _friends = new();
    private readonly ObservableCollection<ThreadVm> _threadVms = new();

    private ScriptDialogEventArgs? _activeDialog;
    private ImThread? _openThread;
    private IDispatcherTimer? _renderTimer;

    // camera gesture state
    private float _yawStart, _pitchStart, _distStart;

    public MainPage()
    {
        InitializeComponent();

        ChatList.ItemsSource = _chatLines;
        NearbyList.ItemsSource = _nearby;
        PeopleList.ItemsSource = _people;
        FriendsList.ItemsSource = _friends;
        ImThreadList.ItemsSource = _threadVms;

        World3D.Cull = _sl.CullEngine;
        World3D.World = _sl.World;

        RangePicker.ItemsSource = new List<string> { "20 m", "40 m", "64 m", "96 m" };
        RangePicker.SelectedIndex = 0;

        _sl.ChatReceived += AppendChat;
        _sl.StatusChanged += msg => StatusLabel.Text = msg;
        _sl.ScriptDialogReceived += ShowScriptDialog;
        _sl.CullEngine.NearbyUpdated += OnNearbyUpdated;
        _sl.World.AvatarsUpdated += OnAvatarsUpdated;
        _sl.World.FriendsUpdated += OnFriendsUpdated;
        _sl.World.ImUpdated += OnImUpdated;
        _sl.World.Notice += AppendChat;
    }

    private void AppendChat(string line)
    {
        _chatLines.Add(line);
        while (_chatLines.Count > 300) _chatLines.RemoveAt(0);
    }

    // ---------------- Login ----------------
    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        var first = FirstNameEntry.Text?.Trim() ?? "";
        var last = string.IsNullOrWhiteSpace(LastNameEntry.Text) ? "Resident" : LastNameEntry.Text.Trim();
        var pass = PasswordEntry.Text ?? "";
        var start = string.IsNullOrWhiteSpace(StartLocationEntry.Text) ? "last" : StartLocationEntry.Text.Trim();

        if (first.Length == 0 || pass.Length == 0)
        {
            LoginStatusLabel.Text = "First name and password are required.";
            return;
        }

        ConnectButton.IsEnabled = false;
        LoginSpinner.IsRunning = true;
        LoginStatusLabel.Text = "Logging in...";

        var (ok, message) = await _sl.LoginAsync(first, last, pass, start);

        LoginSpinner.IsRunning = false;
        ConnectButton.IsEnabled = true;
        LoginStatusLabel.Text = message;

        if (ok)
        {
            LoginPanel.IsVisible = false;
            ViewportPanel.IsVisible = true;
            StatusLabel.Text = message;
            AppendChat($"* {message}");
            StartRenderLoop();
        }
    }

    private void OnShowPwChanged(object? sender, CheckedChangedEventArgs e)
        => PasswordEntry.IsPassword = !e.Value;

    // ---------------- Render loop ----------------
    private void StartRenderLoop()
    {
        _renderTimer = Dispatcher.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(70);   // ~14 fps
        _renderTimer.Tick += (s, e) =>
        {
            if (WorldPanel.IsVisible) World3D.InvalidateSurface();
        };
        _renderTimer.Start();
    }

    private void OnWorldPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _yawStart = World3D.Yaw;
                _pitchStart = World3D.Pitch;
                break;
            case GestureStatus.Running:
                World3D.Yaw = _yawStart - (float)e.TotalX * 0.006f;
                World3D.Pitch = Math.Clamp(_pitchStart + (float)e.TotalY * 0.005f, -0.35f, 1.35f);
                break;
        }
    }

    private void OnWorldPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started) _distStart = World3D.Distance;
        else if (e.Status == GestureStatus.Running)
            World3D.Distance = Math.Clamp(_distStart / (float)Math.Max(e.Scale, 0.05), 1.5f, 60f);
    }

    private void OnRangeChanged(object? sender, EventArgs e)
    {
        float[] ranges = { 20f, 40f, 64f, 96f };
        int i = Math.Clamp(RangePicker.SelectedIndex, 0, ranges.Length - 1);
        _sl.CullEngine.CullRadius = ranges[i];
    }

    // ---------------- Audio stream ----------------
    private async void OnAudioToggle(object? sender, EventArgs e)
    {
        if (_sl.Audio.IsPlaying)
        {
            _sl.Audio.Stop();
            AudioButton.Text = "Audio ▶";
            AudioLabel.Text = "Stopped";
            return;
        }

        var url = _sl.World.ParcelMusicUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            AudioLabel.Text = "No stream on this parcel";
            return;
        }

        AudioButton.IsEnabled = false;
        AudioLabel.Text = "Connecting...";
        var result = await _sl.Audio.PlayAsync(url);
        AudioButton.IsEnabled = true;

        if (_sl.Audio.IsPlaying)
        {
            AudioButton.Text = "Audio ■";
            AudioLabel.Text = string.IsNullOrEmpty(_sl.World.ParcelName)
                ? "Playing parcel stream" : $"♪ {_sl.World.ParcelName}";
        }
        else AudioLabel.Text = result;
    }

    // ---------------- Chat ----------------
    private void OnSendClicked(object? sender, EventArgs e)
    {
        var text = ChatEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _sl.SendLocalChat(text);
        AppendChat($"You: {text}");
        ChatEntry.Text = "";
    }

    // ---------------- Lists ----------------
    private void OnNearbyUpdated(IReadOnlyList<NearbyPrim> prims)
    {
        _nearby.Clear();
        foreach (var p in prims)
            _nearby.Add(new NearbyPrimVm(p.Name, $"{p.Distance:0.0} m"));
    }

    private void OnAvatarsUpdated(IReadOnlyList<NearbyAvatar> avatars)
    {
        _people.Clear();
        foreach (var a in avatars)
            _people.Add(new PersonVm(a.Id, a.Name, $"{a.Distance:0.0} m"));
    }

    private void OnFriendsUpdated(IReadOnlyList<FriendEntry> friends)
    {
        _friends.Clear();
        int online = 0;
        foreach (var f in friends)
        {
            if (f.IsOnline) online++;
            _friends.Add(new FriendVm(f.Id, f.Name,
                f.IsOnline ? Brush.LimeGreen : Brush.Gray));
        }
        FriendsHeader.Text = $"Friends — {online} online / {friends.Count} total";
    }

    private void OnImUpdated(ImThread thread)
    {
        RefreshThreadList();
        if (_openThread != null && _openThread.AgentId == thread.AgentId)
        {
            ImMessages.ItemsSource = thread.Messages;
            thread.Unread = 0;
        }
    }

    private void RefreshThreadList()
    {
        _threadVms.Clear();
        foreach (var t in _sl.World.Threads)
            _threadVms.Add(new ThreadVm(t, t.Name, t.Unread > 0 ? $"{t.Unread} new" : ""));
    }

    // ---------------- IM ----------------
    private void OnPersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not PersonVm p) return;
        PeopleList.SelectedItem = null;
        OpenThread(_sl.World.GetOrCreateThread(p.Id, p.Name));
    }

    private void OnFriendSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not FriendVm f) return;
        FriendsList.SelectedItem = null;
        OpenThread(_sl.World.GetOrCreateThread(f.Id, f.Name));
    }

    private void OnThreadSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ThreadVm t) return;
        ImThreadList.SelectedItem = null;
        OpenThread(t.Thread);
    }

    private void OpenThread(ImThread thread)
    {
        _openThread = thread;
        thread.Unread = 0;
        ImTitle.Text = thread.Name;
        ImMessages.ItemsSource = thread.Messages;
        ShowTab(ImPanel, TabIm);
        ImListView.IsVisible = false;
        ImThreadView.IsVisible = true;
        RefreshThreadList();
    }

    private void OnImBack(object? sender, EventArgs e)
    {
        _openThread = null;
        ImThreadView.IsVisible = false;
        ImListView.IsVisible = true;
        RefreshThreadList();
    }

    private void OnImSend(object? sender, EventArgs e)
    {
        if (_openThread == null) return;
        var text = ImEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _sl.World.SendIm(_openThread, text);
        ImEntry.Text = "";
    }

    // ---------------- Tabs ----------------
    private void ShowTab(Grid panel, Button tab)
    {
        WorldPanel.IsVisible = panel == WorldPanel;
        ChatPanel.IsVisible = panel == ChatPanel;
        PeoplePanel.IsVisible = panel == PeoplePanel;
        FriendsPanel.IsVisible = panel == FriendsPanel;
        ImPanel.IsVisible = panel == ImPanel;

        foreach (var b in new[] { TabWorld, TabChat, TabPeople, TabFriends, TabIm })
            b.BackgroundColor = b == tab ? Color.FromArgb("#2F89D8") : Color.FromArgb("#3A4A5A");
    }

    private void OnTabWorld(object? sender, EventArgs e) => ShowTab(WorldPanel, TabWorld);
    private void OnTabChat(object? sender, EventArgs e) => ShowTab(ChatPanel, TabChat);
    private void OnTabPeople(object? sender, EventArgs e) => ShowTab(PeoplePanel, TabPeople);
    private void OnTabFriends(object? sender, EventArgs e) => ShowTab(FriendsPanel, TabFriends);
    private void OnTabIm(object? sender, EventArgs e)
    {
        RefreshThreadList();
        ShowTab(ImPanel, TabIm);
    }

    // ---------------- LSL blue menu ----------------
    private void ShowScriptDialog(ScriptDialogEventArgs e)
    {
        _activeDialog = e;
        DialogTitleLabel.Text = e.ObjectName;
        DialogMessageLabel.Text = e.Message;

        DialogButtonsLayout.Children.Clear();
        for (int i = 0; i < e.ButtonLabels.Count; i++)
        {
            int index = i;
            string label = e.ButtonLabels[i];
            var btn = new Button
            {
                Text = label,
                FontSize = 13,
                Margin = new Thickness(3),
                MinimumWidthRequest = 96,
                BackgroundColor = Color.FromArgb("#3A6EA5"),
                TextColor = Colors.White
            };
            btn.Clicked += (_, _) =>
            {
                if (_activeDialog is null) return;
                _sl.ReplyToScriptDialog(_activeDialog.Channel, index, label, _activeDialog.ObjectID);
                CloseDialog();
            };
            DialogButtonsLayout.Children.Add(btn);
        }
        DialogOverlay.IsVisible = true;
    }

    private void OnDialogIgnoreClicked(object? sender, EventArgs e) => CloseDialog();

    private void CloseDialog()
    {
        _activeDialog = null;
        DialogOverlay.IsVisible = false;
    }

    // ---------------- view models ----------------
    private sealed record NearbyPrimVm(string Name, string DistanceText);
    private sealed record PersonVm(UUID Id, string Name, string DistanceText);
    private sealed record FriendVm(UUID Id, string Name, Brush StatusColor);
    private sealed record ThreadVm(ImThread Thread, string Name, string Badge);
}

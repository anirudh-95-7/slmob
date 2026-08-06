using System.Collections.ObjectModel;
using LibreMetaverse;
using SLMobileViewer.Rendering;
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
    private readonly ObservableCollection<ThreadVm> _groupVms = new();

    private ScriptDialogEventArgs? _activeDialog;
    private ImThread? _openThread;
    private ImThread? _openGroup;
    private IDispatcherTimer? _renderTimer;

    private Primitive? _selectedPrim;
    private Vector3 _lastDrawnPos = Vector3.Zero;
    private bool _forceRedraw = true;
    private UUID _profileId = UUID.Zero;
    private string _profileName = "";

    public MainPage()
    {
        InitializeComponent();

        ChatList.ItemsSource = _chatLines;
        NearbyList.ItemsSource = _nearby;
        PeopleList.ItemsSource = _people;
        FriendsList.ItemsSource = _friends;
        ImThreadList.ItemsSource = _threadVms;
        GroupThreadList.ItemsSource = _groupVms;

        World3D.Cull = _sl.CullEngine;
        World3D.World = _sl.World;
        World3D.Baker = _sl.Baker;
        World3D.Picked += OnWorldPicked;
        _sl.Baker.Progress += OnBakeProgress;

        RangePicker.ItemsSource = new List<string> { "20 m", "40 m", "64 m", "96 m" };
        RangePicker.SelectedIndex = 0;
        _sl.CullEngine.CullRadius = 20f;

        ApplyThemeToRenderer();

        _sl.ChatReceived += AppendChat;
        _sl.StatusChanged += msg => StatusLabel.Text = msg;
        _sl.ScriptDialogReceived += ShowScriptDialog;
        _sl.CullEngine.NearbyUpdated += OnNearbyUpdated;
        _sl.World.AvatarsUpdated += OnAvatarsUpdated;
        _sl.World.FriendsUpdated += OnFriendsUpdated;
        _sl.World.ImUpdated += OnImUpdated;
        _sl.World.GroupUpdated += OnGroupUpdated;
        _sl.World.ProfileReceived += OnProfileReceived;
        _sl.World.Notice += AppendChat;
    }

    private void AppendChat(string line)
    {
        _chatLines.Add(line);
        while (_chatLines.Count > 300) _chatLines.RemoveAt(0);
    }

    // ---------- theme ----------
    private void OnToggleTheme(object? sender, EventArgs e)
    {
        var app = Application.Current;
        if (app == null) return;
        var current = app.UserAppTheme == AppTheme.Unspecified ? app.RequestedTheme : app.UserAppTheme;
        app.UserAppTheme = current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        ApplyThemeToRenderer();
        _forceRedraw = true;
    }

    private void ApplyThemeToRenderer()
    {
        var app = Application.Current;
        if (app == null) return;
        var t = app.UserAppTheme == AppTheme.Unspecified ? app.RequestedTheme : app.UserAppTheme;
        World3D.LightTheme = t == AppTheme.Light;
    }

    // ---------- login / logout ----------
    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        var first = FirstNameEntry.Text?.Trim() ?? "";
        var last = string.IsNullOrWhiteSpace(LastNameEntry.Text) ? "Resident" : LastNameEntry.Text.Trim();
        var pass = PasswordEntry.Text ?? "";
        var start = string.IsNullOrWhiteSpace(StartLocationEntry.Text) ? "last" : StartLocationEntry.Text.Trim();

        if (first.Length == 0 || pass.Length == 0)
        {
            LoginStatusLabel.Text = "Username and password are required.";
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
            SelfNameLabel.Text = _sl.SelfName;
            StatusLabel.Text = message;
            AppendChat($"* {message}");
            ShowTab(WorldPanel, TabWorld);
            StartRenderLoop();
        }
    }

    private void OnShowPwChanged(object? sender, CheckedChangedEventArgs e)
        => PasswordEntry.IsPassword = !e.Value;

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        bool yes = await DisplayAlert("Log out", "Disconnect from the grid?", "Log out", "Cancel");
        if (!yes) return;

        _renderTimer?.Stop();
        _renderTimer = null;
        _sl.StopAllMovement();
        _sl.Logout();

        _chatLines.Clear(); _nearby.Clear(); _people.Clear();
        _friends.Clear(); _threadVms.Clear(); _groupVms.Clear();
        _selectedPrim = null; _openThread = null; _openGroup = null;
        SelectionCard.IsVisible = false;
        World3D.SelectedLocalId = 0;

        ViewportPanel.IsVisible = false;
        LoginPanel.IsVisible = true;
        LoginStatusLabel.Text = "Logged out.";
    }

    // ---------- render loop ----------
    private void StartRenderLoop()
    {
        // The scene is static, so we only redraw while the user is interacting,
        // while a bake is progressing, or when the avatar has moved.
        _renderTimer = Dispatcher.CreateTimer();
        _renderTimer.Interval = TimeSpan.FromMilliseconds(90);
        _renderTimer.Tick += (s, e) =>
        {
            if (!WorldPanel.IsVisible) return;

            var pos = _sl.CullEngine.AvatarPosition();
            bool moved = Vector3.Distance(pos, _lastDrawnPos) > 0.05f;
            if (moved || World3D.FastMode || _sl.Baker.IsBaking || _forceRedraw)
            {
                _lastDrawnPos = pos;
                _forceRedraw = false;
                World3D.InvalidateSurface();
                CheckDrift();
            }
        };
        _renderTimer.Start();
    }

    private void CheckDrift()
    {
        if (!_sl.Baker.HasScene || _sl.Baker.IsBaking) return;
        float drift = _sl.Baker.DriftFromOrigin();
        if (drift > _sl.Baker.BakedRadius * 0.55f)
            BakeLabel.Text = $"You've moved {drift:0} m from the built area — tap Rebuild";
        BuildButton.Text = _sl.Baker.HasScene ? "Rebuild" : "Build 20 m";
    }

    // ---------- scene baking ----------
    private void OnBuildScene(object? sender, EventArgs e)
    {
        float r = CurrentRange();
        BakeBar.IsVisible = true;
        BakeBar.Progress = 0;
        BakeLabel.Text = "Collecting objects…";
        _sl.Baker.Rebuild(r);
        _forceRedraw = true;
    }

    private void OnExtendScene(object? sender, EventArgs e)
    {
        float r = MathF.Min(CurrentRange() * 2f, 96f);
        SetRangePicker(r);
        BakeBar.IsVisible = true;
        BakeLabel.Text = $"Extending to {r:0} m in the background…";
        _sl.Baker.Extend(r);
        _forceRedraw = true;
    }

    private void OnBakeProgress(BakeProgress p)
    {
        BakeLabel.Text = p.Message;
        BakeBar.Progress = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : 0;
        BakeBar.IsVisible = !p.Finished;
        _forceRedraw = true;

        if (p.Finished)
        {
            BuildButton.Text = "Rebuild";
            ExtendButton.IsVisible = _sl.Baker.BakedRadius < 96f;
            ExtendButton.Text = $"Extend to {MathF.Min(_sl.Baker.BakedRadius * 2f, 96f):0} m";
        }
    }

    private float CurrentRange()
    {
        float[] ranges = { 20f, 40f, 64f, 96f };
        return ranges[Math.Clamp(RangePicker.SelectedIndex, 0, 3)];
    }

    private void SetRangePicker(float r)
    {
        float[] ranges = { 20f, 40f, 64f, 96f };
        for (int i = 0; i < ranges.Length; i++)
            if (Math.Abs(ranges[i] - r) < 0.5f) { RangePicker.SelectedIndex = i; return; }
    }

    // ---------- movement ----------
    private void OnForwardDown(object? sender, EventArgs e) => _sl.SetWalk(true, true, World3D.Yaw);
    private void OnBackDown(object? sender, EventArgs e) => _sl.SetWalk(false, true, World3D.Yaw);
    private void OnTurnLeftDown(object? sender, EventArgs e) => _sl.SetTurn(true, true);
    private void OnTurnRightDown(object? sender, EventArgs e) => _sl.SetTurn(false, true);
    private void OnJumpDown(object? sender, EventArgs e) => _sl.SetUp(true, true);
    private void OnMoveUp(object? sender, EventArgs e) => _sl.StopAllMovement();

    private void OnToggleFly(object? sender, EventArgs e)
    {
        bool flying = _sl.ToggleFly();
        BtnFly.Text = flying ? "Land" : "Fly";
        AppendChat(flying ? "* Flying" : "* Landed");
    }

    private void OnRangeChanged(object? sender, EventArgs e)
    {
        float r = CurrentRange();
        _sl.CullEngine.CullRadius = MathF.Max(_sl.CullEngine.CullRadius, r);
        if (BuildButton != null && !_sl.Baker.HasScene) BuildButton.Text = $"Build {r:0} m";
    }

    // ---------- world picking / object actions ----------
    private void OnWorldPicked(PickResult pick)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (pick.IsAvatar)
            {
                ShowProfile(pick.Id, pick.Name);
                return;
            }

            var prim = _sl.CullEngine.FindByLocalId(pick.LocalId);
            if (prim == null) return;

            _selectedPrim = prim;
            World3D.SelectedLocalId = prim.LocalID;
            _forceRedraw = true;
            _sl.CullEngine.RequestDetails(prim);
            UpdateSelectionCard();
            SelectionCard.IsVisible = true;
        });
    }

    private void UpdateSelectionCard()
    {
        if (_selectedPrim == null) return;
        var p = _selectedPrim;
        var props = _sl.CullEngine.PropsFor(p);
        SelName.Text = _sl.CullEngine.NameFor(p);
        SelDesc.Text = string.IsNullOrWhiteSpace(props?.Description) ? "(no description)" : props!.Description;

        float dist = Vector3.Distance(_sl.CullEngine.AvatarPosition(), p.Position);
        string type;
        try { type = p.Type.ToString(); } catch { type = "Prim"; }
        SelMeta.Text = $"{type} · {dist:0.0} m away · {p.Scale.X:0.##}×{p.Scale.Y:0.##}×{p.Scale.Z:0.##} m";
    }

    private void OnTouchObject(object? sender, EventArgs e)
    {
        if (_selectedPrim == null) return;
        _sl.Touch(_selectedPrim.LocalID);
        AppendChat($"* Touched {_sl.CullEngine.NameFor(_selectedPrim)}");
    }

    private void OnSitObject(object? sender, EventArgs e)
    {
        if (_selectedPrim == null) return;
        _sl.SitOn(_selectedPrim.ID);
        AppendChat($"* Sitting on {_sl.CullEngine.NameFor(_selectedPrim)}");
    }

    private void OnStandUp(object? sender, EventArgs e)
    {
        _sl.StandUp();
        AppendChat("* Stood up");
    }

    private void OnCloseSelection(object? sender, EventArgs e)
    {
        SelectionCard.IsVisible = false;
        _selectedPrim = null;
        World3D.SelectedLocalId = 0;
        _forceRedraw = true;
    }

    // ---------- audio ----------
    private async void OnAudioToggle(object? sender, EventArgs e)
    {
        if (_sl.Audio.IsPlaying)
        {
            _sl.Audio.Stop();
            AudioLabel.Text = "Stopped";
            return;
        }

        var url = _sl.World.ParcelMusicUrl;
        if (string.IsNullOrWhiteSpace(url)) { AudioLabel.Text = "No stream here"; return; }

        AudioButton.IsEnabled = false;
        AudioLabel.Text = "Connecting...";
        var result = await _sl.Audio.PlayAsync(url);
        AudioButton.IsEnabled = true;
        AudioLabel.Text = _sl.Audio.IsPlaying
            ? (string.IsNullOrEmpty(_sl.World.ParcelName) ? "Playing" : $"♪ {_sl.World.ParcelName}")
            : result;
    }

    // ---------- chat ----------
    private void OnSendClicked(object? sender, EventArgs e)
    {
        var text = ChatEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _sl.SendLocalChat(text);
        AppendChat($"You: {text}");
        ChatEntry.Text = "";
    }

    // ---------- lists ----------
    private void OnNearbyUpdated(IReadOnlyList<NearbyPrim> prims)
    {
        _nearby.Clear();
        foreach (var p in prims) _nearby.Add(new NearbyPrimVm(p.Name, $"{p.Distance:0.0} m"));
        if (_selectedPrim != null) UpdateSelectionCard();
    }

    private void OnAvatarsUpdated(IReadOnlyList<NearbyAvatar> avatars)
    {
        _people.Clear();
        foreach (var a in avatars) _people.Add(new PersonVm(a.Id, a.Name, $"{a.Distance:0.0} m"));
    }

    private void OnFriendsUpdated(IReadOnlyList<FriendEntry> friends)
    {
        _friends.Clear();
        int online = 0;
        foreach (var f in friends)
        {
            if (f.IsOnline) online++;
            _friends.Add(new FriendVm(f.Id, f.Name,
                f.IsOnline ? Brush.LimeGreen : Brush.Gray, f.IsOnline ? "online" : ""));
        }
        FriendsHeader.Text = $"FRIENDS — {online} ONLINE / {friends.Count}";
    }

    // ---------- IM ----------
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

    // ---------- groups ----------
    private void OnGroupUpdated(ImThread thread)
    {
        RefreshGroupList();
        if (_openGroup != null && _openGroup.AgentId == thread.AgentId)
        {
            GroupMessages.ItemsSource = thread.Messages;
            thread.Unread = 0;
        }
    }

    private void RefreshGroupList()
    {
        _groupVms.Clear();
        foreach (var t in _sl.World.GroupThreads)
        {
            if (t.AgentId == UUID.Zero) continue;
            _groupVms.Add(new ThreadVm(t, t.Name, t.Unread > 0 ? $"{t.Unread} new" : ""));
        }
    }

    private void OnGroupSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ThreadVm t) return;
        GroupThreadList.SelectedItem = null;
        _openGroup = t.Thread;
        t.Thread.Unread = 0;
        GroupTitle.Text = t.Thread.Name;
        GroupMessages.ItemsSource = t.Thread.Messages;
        GroupListView.IsVisible = false;
        GroupThreadView.IsVisible = true;
        RefreshGroupList();
    }

    private void OnGroupBack(object? sender, EventArgs e)
    {
        _openGroup = null;
        GroupThreadView.IsVisible = false;
        GroupListView.IsVisible = true;
        RefreshGroupList();
    }

    private void OnGroupSend(object? sender, EventArgs e)
    {
        if (_openGroup == null) return;
        var text = GroupEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _sl.World.SendGroupIm(_openGroup, text);
        GroupEntry.Text = "";
    }

    // ---------- profiles ----------
    private void OnPersonSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not PersonVm p) return;
        PeopleList.SelectedItem = null;
        ShowProfile(p.Id, p.Name);
    }

    private void OnFriendSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not FriendVm f) return;
        FriendsList.SelectedItem = null;
        ShowProfile(f.Id, f.Name);
    }

    private void ShowProfile(UUID id, string name)
    {
        _profileId = id;
        _profileName = name;
        ProfName.Text = name;
        ProfBorn.Text = "Loading profile...";
        ProfPartner.Text = "";
        ProfAbout.Text = "";
        ProfRl.Text = "";
        _sl.World.RequestProfile(id);

        WorldPanel.IsVisible = false; ChatPanel.IsVisible = false; PeoplePanel.IsVisible = false;
        FriendsPanel.IsVisible = false; ImPanel.IsVisible = false; GroupsPanel.IsVisible = false;
        ProfilePanel.IsVisible = true;
    }

    private void OnProfileReceived(UUID id, Avatar.AvatarProperties props)
    {
        if (id != _profileId) return;
        ProfBorn.Text = string.IsNullOrWhiteSpace(props.BornOn) ? "" : $"Resident since {props.BornOn}";
        ProfPartner.Text = props.Partner != UUID.Zero ? "Partnered" : "";
        ProfAbout.Text = string.IsNullOrWhiteSpace(props.AboutText) ? "(nothing written)" : props.AboutText;
        ProfRl.Text = string.IsNullOrWhiteSpace(props.FirstLifeText) ? "(nothing written)" : props.FirstLifeText;
    }

    private void OnProfileBack(object? sender, EventArgs e)
    {
        ProfilePanel.IsVisible = false;
        ShowTab(PeoplePanel, TabPeople);
    }

    private void OnProfileIm(object? sender, EventArgs e)
    {
        if (_profileId == UUID.Zero) return;
        ProfilePanel.IsVisible = false;
        OpenThread(_sl.World.GetOrCreateThread(_profileId, _profileName));
    }

    private void OnProfileFriend(object? sender, EventArgs e)
    {
        if (_profileId == UUID.Zero) return;
        try
        {
            _sl.Client.Friends.OfferFriendship(_profileId);
            AppendChat($"* Friendship offered to {_profileName}");
        }
        catch (Exception ex) { AppendChat($"* Friend offer failed: {ex.Message}"); }
    }

    // ---------- tabs ----------
    private void ShowTab(Layout panel, Button tab)
    {
        ProfilePanel.IsVisible = false;
        WorldPanel.IsVisible = panel == WorldPanel;
        ChatPanel.IsVisible = panel == ChatPanel;
        PeoplePanel.IsVisible = panel == PeoplePanel;
        FriendsPanel.IsVisible = panel == FriendsPanel;
        ImPanel.IsVisible = panel == ImPanel;
        GroupsPanel.IsVisible = panel == GroupsPanel;

        foreach (var b in new[] { TabWorld, TabChat, TabPeople, TabFriends, TabIm, TabGroups })
            b.Opacity = b == tab ? 1.0 : 0.55;
    }

    private void OnTabWorld(object? sender, EventArgs e) => ShowTab(WorldPanel, TabWorld);
    private void OnTabChat(object? sender, EventArgs e) => ShowTab(ChatPanel, TabChat);
    private void OnTabPeople(object? sender, EventArgs e) => ShowTab(PeoplePanel, TabPeople);
    private void OnTabFriends(object? sender, EventArgs e) => ShowTab(FriendsPanel, TabFriends);
    private void OnTabIm(object? sender, EventArgs e) { RefreshThreadList(); ShowTab(ImPanel, TabIm); }
    private void OnTabGroups(object? sender, EventArgs e) { RefreshGroupList(); ShowTab(GroupsPanel, TabGroups); }

    // ---------- blue menu ----------
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
                CornerRadius = 10
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

    // ---------- view models ----------
    private sealed record NearbyPrimVm(string Name, string DistanceText);
    private sealed record PersonVm(UUID Id, string Name, string DistanceText);
    private sealed record FriendVm(UUID Id, string Name, Brush StatusColor, string StatusText);
    private sealed record ThreadVm(ImThread Thread, string Name, string Badge);
}

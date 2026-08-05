using System.Collections.ObjectModel;
using LibreMetaverse;
using SLMobileViewer.Services;

namespace SLMobileViewer;

public partial class MainPage : ContentPage
{
    private readonly SecondLifeService _sl = SecondLifeService.Instance;

    private readonly ObservableCollection<string> _chatLines = new();
    private readonly ObservableCollection<NearbyPrimVm> _nearby = new();

    private ScriptDialogEventArgs? _activeDialog;

    public MainPage()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chatLines;
        NearbyList.ItemsSource = _nearby;

        _sl.ChatReceived += AppendChat;
        _sl.StatusChanged += msg => StatusLabel.Text = msg;
        _sl.ScriptDialogReceived += ShowScriptDialog;
        _sl.CullEngine.NearbyUpdated += OnNearbyUpdated;
    }

    private void AppendChat(string line)
    {
        _chatLines.Add(line);
        while (_chatLines.Count > 300) _chatLines.RemoveAt(0);
    }

    // ---------- Login ----------
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
        }
    }

    // ---------- Chat ----------
    private void OnSendClicked(object? sender, EventArgs e)
    {
        var text = ChatEntry.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _sl.SendLocalChat(text);
        AppendChat($"You: {text}");
        ChatEntry.Text = "";
    }

    // ---------- 20 m nearby list ----------
    private void OnNearbyUpdated(IReadOnlyList<NearbyPrim> prims)
    {
        _nearby.Clear();
        foreach (var p in prims)
            _nearby.Add(new NearbyPrimVm(p.Name, $"{p.Distance:0.0} m"));
    }

    // ---------- LSL blue menu ----------
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

    // ---------- Logout ----------
    private void OnLogoutClicked(object? sender, EventArgs e)
    {
        _sl.Logout();
        _nearby.Clear();
        _chatLines.Clear();
        ViewportPanel.IsVisible = false;
        LoginPanel.IsVisible = true;
        LoginStatusLabel.Text = "Logged out.";
    }

    private void OnShowPwChanged(object? sender, CheckedChangedEventArgs e)
        => PasswordEntry.IsPassword = !e.Value;

    private sealed record NearbyPrimVm(string Name, string DistanceText);
}

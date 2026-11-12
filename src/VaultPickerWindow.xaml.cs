using System.Windows;
using System.Windows.Controls;
using static OnScreenKeyboard.NativeMethods;

namespace OnScreenKeyboard;

public partial class VaultPickerWindow : Window
{
    private readonly Vault _vault;
    private readonly nint _target;
    private readonly uint _targetProcess;
    private bool _inserting;
    private bool _closed;
    public event Action<VaultItem?>? EditRequested;
    public event Action? BeforeInsert;
    public event Action? Inserted;

    public VaultPickerWindow(Vault vault, nint target)
    {
        _vault = vault;
        _target = target;
        GetWindowThreadProcessId(target, out _targetProcess);
        InitializeComponent();
        TargetText.Text = ValidTarget() ? $"Insert into: {WindowTitle(target)}" : "Select a text field in your app, then reopen Vault.";
        RefreshList();
        _vault.Changed += RefreshList;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private bool ValidTarget() => _target != 0 && IsWindow(_target)
        && GetWindowThreadProcessId(_target, out var process) != 0
        && process == _targetProcess && process != Environment.ProcessId;

    private void RefreshList()
    {
        var items = _vault.Search(SearchBox.Text);
        ItemList.ItemsSource = items;
        Status.Text = _vault.Items.Count == 0 ? "Your vault is empty. Choose + Add item to save your first item."
            : items.Count == 0 ? "No matching items." : "";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (ItemList != null && Status != null) RefreshList();
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VaultItem item } && !_vault.ToggleFavorite(item))
            Status.Text = "Could not save this favorite. Please try again.";
    }

    private void Manage_Click(object sender, RoutedEventArgs e) => Edit(null);
    private void Edit_Click(object sender, RoutedEventArgs e) => Edit((VaultItem)((Button)sender).DataContext);
    private void Edit(VaultItem? item)
    {
        EditRequested?.Invoke(item);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: VaultItem item }) return;
        if (MessageBox.Show(this, $"Delete “{item.Name}” from the vault?", "Delete saved item",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!_vault.Remove(item)) Status.Text = "Could not delete this item. Please try again.";
    }

    private async void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (_inserting || sender is not Button { DataContext: VaultItem item }) return;
        if (!ValidTarget())
        {
            Status.Text = "Select a text field in your app, then close and reopen Vault.";
            return;
        }
        _inserting = true;
        IsEnabled = false;
        try
        {
            if (IsIconic(_target)) ShowWindowAsync(_target, 9);
            SetForegroundWindow(_target);
            // Foreground activation is asynchronous across application threads.
            for (int i = 0; i < 10 && GetForegroundWindow() != _target; i++)
                await Task.Delay(25);
            await Task.Delay(75);
            if (_closed) return;
            if (!ValidTarget() || GetForegroundWindow() != _target)
            {
                Activate();
                Status.Text = "Could not focus your app. Select its text field, then reopen Vault and try again.";
                return;
            }
            BeforeInsert?.Invoke();
            if (!TypeText(item.Value))
            {
                Activate();
                Status.Text = "Windows could not insert all the text. Check your app before retrying. Apps running as administrator may block insertion.";
                return;
            }
            Status.Text = "Inserted. Choose another item or close the vault when finished.";
            Inserted?.Invoke();
        }
        finally
        {
            IsEnabled = true;
            _inserting = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _vault.Changed -= RefreshList;
        base.OnClosed(e);
    }
}

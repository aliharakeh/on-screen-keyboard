using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OnScreenKeyboard;

public partial class VaultWindow : Window
{
    private readonly Vault _vault;
    private VaultItem? _editing;
    private (string Name, string Value, bool Secret) _original = ("", "", false);
    private string CurrentValue => SecretValueBox.Visibility == Visibility.Visible ? SecretValueBox.Password : ValueBox.Text;
    private (string, string, bool) Current => (NameBox.Text, CurrentValue, SecretBox.IsChecked == true);

    public VaultWindow(Vault vault, string? prefill = null)
    {
        _vault = vault;
        InitializeComponent();
        StartNew(prefill);
        Loaded += (_, _) => NameBox.Focus();
    }

    private bool CanDiscard() => Current == _original || MessageBox.Show(this,
        "Discard your unsaved changes?", "Unsaved item", MessageBoxButton.YesNo,
        MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void StartNew(string? prefill = null)
    {
        if (!CanDiscard()) return;
        _editing = null;
        LoadForm(prefill ?? "", prefill ?? "", false);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void LoadForm(string name, string value, bool secret)
    {
        Title = _editing == null ? "New vault item" : "Edit vault item";
        FormTitle.Text = _editing == null ? "New item" : "Edit item";
        NameBox.Text = name;
        ValueBox.Text = value;
        SecretValueBox.Password = value;
        SecretBox.IsChecked = secret;
        RevealBox.IsChecked = false;
        UpdateSecretDisplay();
        Status.Text = "";
        _original = Current;
    }

    private void UpdateSecretDisplay()
    {
        bool secret = SecretBox.IsChecked == true;
        bool masked = secret && RevealBox.IsChecked != true;
        RevealBox.Visibility = secret ? Visibility.Visible : Visibility.Collapsed;
        SecretValueBox.Visibility = masked ? Visibility.Visible : Visibility.Collapsed;
        ValueBox.Visibility = masked ? Visibility.Collapsed : Visibility.Visible;
        // Keep plaintext out of the hidden editor while a secret is masked.
        if (masked) ValueBox.Clear();
        else SecretValueBox.Clear();
    }

    private void SecretDisplay_Changed(object sender, RoutedEventArgs e)
    {
        var value = CurrentValue;
        ValueBox.Text = value;
        SecretValueBox.Password = value;
        if (SecretBox.IsChecked != true) RevealBox.IsChecked = false;
        UpdateSecretDisplay();
    }

    public void EditItem(VaultItem item)
    {
        if (!CanDiscard()) return;
        _editing = item;
        LoadForm(item.Name, item.Value, item.Secret);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var value = CurrentValue;
        if (name.Length == 0 || value.Length == 0)
        {
            Status.Foreground = Brushes.Orange;
            Status.Text = "Enter a shortcut and the text to insert.";
            return;
        }
        bool saved;
        if (_editing != null)
        {
            if (!_vault.Items.Contains(_editing))
            {
                Status.Text = "This item was deleted. Close this form and add a new item.";
                return;
            }
            var previous = (_editing.Name, _editing.Value, _editing.Secret);
            _editing.Name = name;
            _editing.Value = value;
            _editing.Secret = SecretBox.IsChecked == true;
            saved = _vault.Save();
            if (!saved) (_editing.Name, _editing.Value, _editing.Secret) = previous;
        }
        else
        {
            saved = _vault.Add(new VaultItem { Name = name, Value = value, Secret = SecretBox.IsChecked == true });
        }
        if (!saved)
        {
            Status.Foreground = Brushes.Orange;
            Status.Text = "Could not save this item. Your changes are still here; please try again.";
            return;
        }
        _original = Current;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!CanDiscard()) e.Cancel = true;
        base.OnClosing(e);
    }
}

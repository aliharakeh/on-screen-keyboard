using System.Windows;
using System.Windows.Controls;

namespace OnScreenKeyboard;

public partial class VaultWindow : Window
{
    private readonly Vault _vault;
    private VaultItem? _editing;

    public VaultWindow(Vault vault, string? prefill = null)
    {
        InitializeComponent();
        _vault = vault;
        RefreshList();
        StartNew(prefill);
    }

    /// <summary>Pre-fills the form with a new word (used by the keyboard's "save current word" button).</summary>
    public void StartNew(string? prefill = null)
    {
        _editing = null;
        ItemList.SelectedItem = null;
        FormTitle.Text = "New item";
        NameBox.Text = prefill ?? "";
        ValueBox.Text = prefill ?? "";
        SecretBox.IsChecked = false;
        DeleteButton.IsEnabled = false;
        Status.Text = "";
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void RefreshList()
    {
        ItemList.ItemsSource = null;
        ItemList.ItemsSource = _vault.Items;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ItemList.SelectedItem is not VaultItem item) return;
        _editing = item;
        FormTitle.Text = "Edit item";
        NameBox.Text = item.Name;
        ValueBox.Text = item.Value;
        SecretBox.IsChecked = item.Secret;
        DeleteButton.IsEnabled = true;
        Status.Text = "";
    }

    private void New_Click(object sender, RoutedEventArgs e) => StartNew();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0 || ValueBox.Text.Length == 0)
        {
            Status.Foreground = System.Windows.Media.Brushes.Orange;
            Status.Text = "Name and value are required.";
            return;
        }
        if (_editing != null)
        {
            _editing.Name = name;
            _editing.Value = ValueBox.Text;
            _editing.Secret = SecretBox.IsChecked == true;
            _vault.Save();
        }
        else
        {
            _editing = new VaultItem { Name = name, Value = ValueBox.Text, Secret = SecretBox.IsChecked == true };
            _vault.Add(_editing);
        }
        var saved = _editing;
        RefreshList();
        ItemList.SelectedItem = saved;
        Status.Foreground = System.Windows.Media.Brushes.LightGreen;
        Status.Text = "Saved.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        _vault.Remove(_editing);
        RefreshList();
        StartNew();
    }
}

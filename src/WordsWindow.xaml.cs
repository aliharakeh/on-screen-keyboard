using System.Windows;
using System.Windows.Controls;

namespace OnScreenKeyboard;

public record WordRow(string Word, string Detail, bool Removable);

/// <summary>Browse the built-in English/Arabic word lists and the words learned from typing.</summary>
public partial class WordsWindow : Window
{
    private readonly Predictor _predictor;
    private List<WordRow> _rows = [];

    public WordsWindow(Predictor predictor)
    {
        _predictor = predictor;
        InitializeComponent();
        EnglishTab.Content = $"English ({Predictor.BuiltInWords(arabic: false).Count:N0})";
        ArabicTab.Content = $"العربية ({Predictor.BuiltInWords(arabic: true).Count:N0})";
        MineTab.IsChecked = true;
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Reloads the current list, e.g. after learned words were cleared.</summary>
    public void Refresh()
    {
        MineTab.Content = $"My words ({_predictor.Learned().Count():N0})";
        if (MineTab.IsChecked == true)
            _rows = _predictor.Learned().Select(w => new WordRow(w.Word, w.Count == 1 ? "typed once" : $"typed {w.Count}×", true)).ToList();
        else
            _rows = Predictor.BuiltInWords(arabic: ArabicTab.IsChecked == true).Select((w, i) => new WordRow(w, $"#{i + 1:N0}", false)).ToList();
        Filter();
    }

    private void Filter()
    {
        var query = SearchBox.Text.Trim();
        var shown = query.Length == 0 ? _rows
            : _rows.Where(r => r.Word.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        WordList.ItemsSource = shown;
        Status.Text = MineTab.IsChecked == true && _rows.Count == 0
            ? "No learned words yet. Words you type on the keyboard appear here."
            : query.Length == 0 ? $"{_rows.Count:N0} words" : $"{shown.Count:N0} of {_rows.Count:N0} words match";
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) Refresh();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var row = (WordRow)((Button)sender).DataContext;
        _predictor.Remove(row.Word);
        _predictor.Save();
        Refresh();
    }
}

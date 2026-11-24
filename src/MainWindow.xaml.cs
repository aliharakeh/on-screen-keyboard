using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static OnScreenKeyboard.NativeMethods;

namespace OnScreenKeyboard;

public partial class MainWindow : Window
{
    private const int ArabicPrimaryLang = 0x01, EnglishPrimaryLang = 0x09;

    private static readonly Brush KeyBrush = Frozen("#FF3C3C3C");
    private static readonly Brush ModifierBrush = Frozen("#FF2A2A2A");
    private static readonly Brush ActiveBrush = Frozen("#FF2D7DD2");

    private readonly Settings _settings = Settings.Load();
    private readonly Vault _vault = Vault.Load();
    private VaultWindow? _vaultWindow;
    private VaultPickerWindow? _vaultPicker;
    private nint _lastTarget;
    private readonly List<(KeyDef Def, Button Button)> _keys = [];
    private readonly HashSet<KeyDef> _held = [];
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _repeat = new();
    private Panel _fnRow = null!, _numRow = null!, _navRow = null!;
    private readonly Dictionary<Panel, RowDefinition> _rowDefs = [];
    private double _baseFont = 20;
    private KeyDef? _repeating;
    private Lang _lang = Lang.English;

    public MainWindow()
    {
        InitializeComponent();
        BuildKeyboard();
        RestoreSettings();

        _repeat.Tick += (_, _) =>
        {
            _repeat.Interval = TimeSpan.FromMilliseconds(40);
            if (_repeating != null)
            {
                Tap(_repeating);
                TrackWord(_repeating);
            }
        };
        _poll.Tick += (_, _) => { RememberTarget(); DetectLanguage(); Refresh(); };
        _poll.Start();
        _vault.Changed += ShowSuggestions;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _predictor.Save(); };
        DetectLanguage();
        Refresh();
        Loaded += (_, _) => AnimateIn();
        LocationChanged += (_, _) => RepositionSuggestions();
        SizeChanged += (_, _) => RepositionSuggestions();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        MakeNoActivate(this);
    }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    // ---- Layout -----------------------------------------------------------------

    private void BuildKeyboard()
    {
        _fnRow = AddRow(KeyboardLayouts.FunctionRow());
        _numRow = AddRow(KeyboardLayouts.NumberRow());
        AddRow(KeyboardLayouts.QwertyRow());
        AddRow(KeyboardLayouts.HomeRow());
        AddRow(KeyboardLayouts.BottomLetterRow());
        AddRow(KeyboardLayouts.SpaceRow());
        AddSideRow([new KeyDef { En = "Esc", EnShift = "Esc", Ar = "Esc", ArShift = "Esc", Scan = 1 },
            new KeyDef { En = "Delete", EnShift = "Delete", Ar = "Delete", ArShift = "Delete", Vk = 0x2E, Extended = true, Repeat = true }]);
        _navRow = new Grid();
        AddSideRow(KeyboardLayouts.NavRow(), (Grid)_navRow);
    }

    private void AddSideRow(List<KeyDef> defs, Grid? grid = null)
    {
        grid ??= new Grid();
        for (int i = 0; i < defs.Count; i += 2)
            AddRow(defs.Skip(i).Take(2).ToList(), grid);
        NavigationKeys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(defs.Count / 2, GridUnitType.Star) });
        Grid.SetRow(grid, NavigationKeys.RowDefinitions.Count - 1);
        NavigationKeys.Children.Add(grid);
    }

    private Panel AddRow(List<KeyDef> defs, Grid? host = null)
    {
        host ??= KeyRows;
        var grid = new Grid();
        for (int i = 0; i < defs.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(defs[i].Width, GridUnitType.Star) });
            var button = new Button { Style = (Style)FindResource("KeyStyle"), Tag = defs[i] };
            button.PreviewMouseLeftButtonDown += Key_Down;
            button.PreviewMouseLeftButtonUp += Key_Up;
            button.LostMouseCapture += Key_Up;
            Grid.SetColumn(button, i);
            grid.Children.Add(button);
            _keys.Add((defs[i], button));
        }
        var rowDef = new RowDefinition { Height = new GridLength(1, GridUnitType.Star) };
        host.RowDefinitions.Add(rowDef);
        Grid.SetRow(grid, host.RowDefinitions.Count - 1);
        host.Children.Add(grid);
        if (host == KeyRows) _rowDefs[grid] = rowDef;
        return grid;
    }

    // ---- Key handling -----------------------------------------------------------

    private void Key_Down(object sender, MouseButtonEventArgs e) => PressKey((KeyDef)((Button)sender).Tag);

    /// <summary>Presses a key as if its button was pressed; repeating keys keep repeating until <see cref="Key_Up"/>.</summary>
    private void PressKey(KeyDef def)
    {
        if (def.IsModifier && def.Kind != KeyKind.Caps)
        {
            ToggleHeld(def);
        }
        else
        {
            Tap(def);
            TrackWord(def);
            if (def.Repeat)
            {
                _repeating = def;
                _repeat.Interval = TimeSpan.FromMilliseconds(400);
                _repeat.Start();
            }
            if (!def.IsModifier) ReleaseHeld();
        }
        Refresh();
    }

    // ---- Typed-word tracking ----------------------------------------------------

    private static readonly Brush CorrectionBrush = Frozen("#FFB8742A");
    private static readonly Brush PredictionBrush = Frozen("#FF4A4A4A");

    private readonly Predictor _predictor = Predictor.Load();
    private readonly SpellChecker _speller = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    private string _word = "";
    /// <summary>Last word completed with Space, used for next-word prediction.</summary>
    private string? _prevWord;
    /// <summary>The word just completed with Space; Backspace removes the space and resumes editing it.</summary>
    private Committed? _resume;
    /// <summary>Spelling suggestions for the word just completed (shown until the next key press).</summary>
    private List<string> _corrections = [];
    /// <summary>Set right after an auto-correction so it can be undone with one tap.</summary>
    private string? _undoOriginal;

    /// <param name="Raw">Text in the field before the trailing space, e.g. "(hello,".</param>
    /// <param name="Lead">Characters before the word, e.g. "(".</param>
    /// <param name="Trail">Characters after the word, e.g. ",".</param>
    private sealed record Committed(string Raw, string Lead, string Core, string Trail, string? PrevBefore, bool Learned);

    private bool IsUpper(KeyDef def)
    {
        bool shift = _held.Any(k => k.Kind == KeyKind.Shift);
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        return shift ^ (caps && def.Letter && _lang == Lang.English);
    }

    /// <summary>Tracks the word being typed from on-screen key presses and shows it in the title bar.</summary>
    private void TrackWord(KeyDef def)
    {
        if (def.Kind == KeyKind.Caps) return;
        _corrections = [];
        _undoOriginal = null;
        var label = def.Label(Lang.English, false);
        if (def.Vk != 0 || _held.Any(k => k.Kind is KeyKind.Ctrl or KeyKind.Alt or KeyKind.Win))
        {
            // Navigation/shortcuts move the caret somewhere we can't see.
            _word = "";
            _prevWord = null;
            _resume = null;
        }
        else if (label == "⌫")
        {
            if (_word.Length > 0) _word = _word[..^1];
            else if (_resume is { } last)
            {
                // The space after the last word was deleted: continue editing that word.
                _word = last.Raw;
                _prevWord = last.PrevBefore;
                if (last.Learned) _predictor.Forget(last.Core);
            }
            else _prevWord = null;
            _resume = null;
        }
        else if (def.Letter || label.Length == 1)
        {
            _word += def.Label(_lang, IsUpper(def));
            _resume = null;
        }
        else
        {
            // Space, Enter, Tab, Esc, F-keys end the word.
            var ended = _word;
            _word = "";
            CommitWord(ended, bySpace: def.Scan == 57);
        }
        if (_word.Length > 40) _word = _word[^40..];
        UpdateWordText();
        ShowSuggestions();
    }

    private void UpdateWordText()
    {
        WordText.Text = _word.Length > 0 ? _word : "On-Screen Keyboard";
        WordText.Foreground = _word.Length > 0 ? Brushes.White : Frozen("#FFAAAAAA");
    }

    /// <summary>Forgets the word context, e.g. after inserting text we did not type key by key.</summary>
    private void ResetWord()
    {
        _word = "";
        _prevWord = null;
        _resume = null;
        _corrections = [];
        _undoOriginal = null;
        UpdateWordText();
        ShowSuggestions();
    }

    private static bool IsWordChar(char c) =>
        char.IsLetter(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark;

    /// <summary>Splits "(don't," into "(", "don't", ","; Core is empty unless the rest is only punctuation.</summary>
    private static (string Lead, string Core, string Trail) SplitWord(string raw)
    {
        int end = raw.Length;
        while (end > 0 && !IsWordChar(raw[end - 1])) end--;
        int start = end;
        while (start > 0 && (IsWordChar(raw[start - 1]) || raw[start - 1] == '\'')) start--;
        while (start < end && raw[start] == '\'') start++;
        string lead = raw[..start], trail = raw[end..];
        bool clean = !lead.Any(char.IsLetterOrDigit) && !trail.Any(char.IsLetterOrDigit);
        return clean ? (lead, raw[start..end], trail) : (raw, "", "");
    }

    /// <summary>Handles a word that was just ended by a key press (the key itself was already sent).</summary>
    private void CommitWord(string raw, bool bySpace)
    {
        var (lead, core, trail) = SplitWord(raw);
        var prevBefore = _prevWord;
        _prevWord = null;
        _resume = null;
        if (core.Length == 0) return;

        if (bySpace && SpellToggle.IsChecked == true && core.Length >= 2 && !_predictor.IsKnown(core)
            && _speller.Check(core) is { } result)
        {
            if (AutoCorrectToggle.IsChecked == true && result.Replacement != null)
            {
                ReplaceTyped(raw.Length + 1, lead + result.Replacement + trail + " ");
                _undoOriginal = core;
                core = result.Replacement;
            }
            else _corrections = result.Suggestions.Take(3).ToList();
        }
        Accept(lead, core, trail, prevBefore, bySpace);
    }

    /// <summary>Records a finished word: learning, next-word context and Backspace-to-resume.</summary>
    private void Accept(string lead, string core, string trail, string? prevBefore, bool bySpace)
    {
        bool learned = false;
        if (LearnToggle.IsChecked == true && !FocusedFieldIsPassword())
        {
            _predictor.Learn(prevBefore, core);
            learned = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        }
        if (!bySpace) return;
        _resume = new Committed(lead + core + trail, lead, core, trail, prevBefore, learned);
        bool endsSentence = trail.IndexOfAny(['.', '!', '?', '؟', ':', ';', '؛']) >= 0;
        _prevWord = endsSentence ? null : core;
    }

    /// <summary>Never learn passwords: asks UI Automation whether the focused field is a password box.</summary>
    private static bool FocusedFieldIsPassword()
    {
        try
        {
            var task = Task.Run(() => System.Windows.Automation.AutomationElement.FocusedElement?.Current.IsPassword == true);
            return !task.Wait(250) || task.Result; // unknown counts as a password
        }
        catch { return true; }
    }

    private static void ReplaceTyped(int backspaces, string text)
    {
        for (int i = 0; i < backspaces; i++)
        {
            SendKey(VK_BACK, 0, 0);
            SendKey(VK_BACK, 0, KEYEVENTF_KEYUP);
        }
        TypeText(text);
    }

    // ---- Suggestions (vault, spelling, predictions) ------------------------------

    private void ShowSuggestions()
    {
        Suggestions.Children.Clear();
        if (_undoOriginal != null)
            AddChip($"↶ {_undoOriginal}", CorrectionBrush, "Undo auto-correct", Undo_Click);
        foreach (var correction in _corrections)
            AddChip(correction, CorrectionBrush, $"Replace “{_resume?.Core}” with “{correction}”", Correction_Click);
        foreach (var item in _vault.Match(_word))
        {
            var content = item.Secret ? $"🔒 {item.Name}" : item.Name == item.Value ? item.Value : $"{item.Name} → {item.Value}";
            AddChip(content, ActiveBrush, null, Suggestion_Click).Tag = item;
        }
        if (PredictToggle.IsChecked == true)
        {
            var (_, core, trail) = SplitWord(_word);
            var words = _word.Length == 0
                ? _prevWord != null ? _predictor.PredictNext(_prevWord) : []
                : core.Length > 0 && trail.Length == 0 ? _predictor.Complete(core, _prevWord) : [];
            foreach (var word in words.Where(w => !_corrections.Contains(w)))
                AddChip(word, PredictionBrush, null, Prediction_Click);
        }
        SuggestionPopup.IsOpen = Suggestions.Children.Count > 0 && IsVisible && WindowState == WindowState.Normal;
    }

    private Button AddChip(string content, Brush background, string? toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("KeyStyle"),
            Content = content,
            FontSize = 14,
            Padding = new Thickness(12, 3, 12, 3),
            Background = background,
            ToolTip = toolTip,
            Tag = content,
        };
        button.Click += click;
        Suggestions.Children.Add(button);
        return button;
    }

    private void RepositionSuggestions()
    {
        if (!SuggestionPopup.IsOpen) return;
        // WPF popups do not automatically follow their window when it moves.
        SuggestionPopup.HorizontalOffset += 1;
        SuggestionPopup.HorizontalOffset -= 1;
    }

    /// <summary>Replaces the word typed so far with the chosen vault value.</summary>
    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        var item = (VaultItem)((Button)sender).Tag;
        ReleaseHeld();
        ReplaceTyped(_word.Length, item.Value);
        ResetWord();
    }

    /// <summary>Completes the current word (or adds the predicted next word) followed by a space.</summary>
    private void Prediction_Click(object sender, RoutedEventArgs e)
    {
        var word = (string)((Button)sender).Tag;
        ReleaseHeld();
        var (lead, core, _) = SplitWord(_word);
        ReplaceTyped(core.Length, word + " ");
        var prevBefore = _prevWord;
        _word = "";
        _corrections = [];
        _undoOriginal = null;
        Accept(lead, word, "", prevBefore, bySpace: true);
        UpdateWordText();
        ShowSuggestions();
    }

    /// <summary>Swaps the misspelled word that was just typed for the chosen suggestion.</summary>
    private void Correction_Click(object sender, RoutedEventArgs e)
    {
        if (_resume is not { } last) return;
        var word = (string)((Button)sender).Tag;
        ReleaseHeld();
        ReplaceTyped(last.Raw.Length + 1, last.Lead + word + last.Trail + " ");
        if (last.Learned) _predictor.Forget(last.Core);
        _corrections = [];
        Accept(last.Lead, word, last.Trail, last.PrevBefore, bySpace: true);
        ShowSuggestions();
    }

    /// <summary>Restores the word the user typed before auto-correct changed it, and stops correcting it.</summary>
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_resume is not { } last || _undoOriginal is not { } original) return;
        ReleaseHeld();
        ReplaceTyped(last.Raw.Length + 1, last.Lead + original + last.Trail + " ");
        if (last.Learned) _predictor.Forget(last.Core);
        _undoOriginal = null;
        Accept(last.Lead, original, last.Trail, last.PrevBefore, bySpace: true);
        if (_resume?.Learned == true) _predictor.MarkKnown(original);
        ShowSuggestions();
    }

    private void ClearLearned_Click(object sender, RoutedEventArgs e)
    {
        OptionsButton.IsChecked = false;
        if (MessageBox.Show(this, "Forget all words learned from your typing?", "Clear learned words",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _predictor.Clear();
        _wordsWindow?.Refresh();
        ResetWord();
    }

    private WordsWindow? _wordsWindow;

    private void BrowseWords_Click(object sender, RoutedEventArgs e)
    {
        OptionsButton.IsChecked = false;
        if (_wordsWindow != null)
        {
            _wordsWindow.Activate();
            return;
        }
        _wordsWindow = new WordsWindow(_predictor) { Owner = this };
        _wordsWindow.Closed += (_, _) => _wordsWindow = null;
        _wordsWindow.Show();
    }

    // ---- Voice typing -----------------------------------------------------------

    private Shortcut _voice = Shortcut.Parse(Shortcut.WindowsVoiceTyping)!;
    private bool _voiceHeld;

    private void UpdateVoiceLabels()
    {
        VoiceButton.ToolTip = $"Voice typing ({_voice}). Hold for push-to-talk apps.";
        VoiceShortcutButton.Content = $"Shortcut: {_voice}  ·  Change…";
        _dock?.SetVoiceTooltip((string)VoiceButton.ToolTip);
    }

    /// <summary>Presses the voice shortcut while the button is held, so both toggle and push-to-talk apps work.</summary>
    private void Voice_Down(object sender, MouseButtonEventArgs e) => PressVoice();

    private void PressVoice()
    {
        if (_voiceHeld) return;
        OptionsButton.IsChecked = false;
        ReleaseHeld();
        ResetWord(); // Dictated text is not tracked.
        _voiceHeld = true;
        _voice.Press();
        Refresh();
    }

    private void Voice_Up(object sender, EventArgs e)
    {
        if (!_voiceHeld) return;
        _voiceHeld = false;
        _voice.Release();
    }

    private void VoiceShortcut_Click(object sender, RoutedEventArgs e)
    {
        OptionsButton.IsChecked = false;
        var dialog = new VoiceShortcutWindow(_voice) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result == null) return;
        _voice = dialog.Result;
        _settings.VoiceShortcut = _voice.ToString();
        UpdateVoiceLabels();
    }

    private void RememberTarget()
    {
        var target = GetForegroundWindow();
        GetWindowThreadProcessId(target, out var process);
        if (target != 0 && process != Environment.ProcessId) _lastTarget = target;
    }

    private void Vault_Click(object sender, RoutedEventArgs e)
    {
        OptionsButton.IsChecked = false;
        SuggestionPopup.IsOpen = false;
        ReleaseHeld();
        RememberTarget();
        if (_vaultWindow != null)
        {
            _vaultWindow.Activate();
            return;
        }
        if (_vaultPicker != null)
        {
            _vaultPicker.Activate();
            return;
        }
        _vaultPicker = new VaultPickerWindow(_vault, _lastTarget) { Owner = (Window?)_dock ?? this };
        if (_dock != null)
        {
            // The keyboard is minimized: centre the picker on the docked monitor instead of on the tiny toolbar.
            _vaultPicker.WindowStartupLocation = WindowStartupLocation.Manual;
            _vaultPicker.Left = _dockArea.Left + (_dockArea.Width - _vaultPicker.Width) / 2;
            _vaultPicker.Top = _dockArea.Top + (_dockArea.Height - _vaultPicker.Height) / 2;
        }
        _vaultPicker.Closed += (_, _) => _vaultPicker = null;
        _vaultPicker.EditRequested += item => OpenVault(null, item);
        _vaultPicker.BeforeInsert += ReleaseHeld;
        _vaultPicker.Inserted += ResetWord;
        _vaultPicker.Show();
    }

    private void SaveWord_Click(object sender, RoutedEventArgs e) => OpenVault(_word.Length > 0 ? _word : null);

    private void OpenVault(string? prefill, VaultItem? item = null)
    {
        OptionsButton.IsChecked = false;
        SuggestionPopup.IsOpen = false;
        if (_vaultWindow == null)
        {
            var picker = _vaultPicker;
            _vaultWindow = new VaultWindow(_vault, prefill) { Owner = (Window?)picker ?? this };
            if (item != null) _vaultWindow.EditItem(item);
            if (picker != null) picker.IsEnabled = false;
            _vaultWindow.Closed += (_, _) =>
            {
                _vaultWindow = null;
                if (picker != null && picker.IsLoaded)
                {
                    picker.IsEnabled = true;
                    picker.Activate();
                }
            };
            _vaultWindow.Show();
        }
        else
        {
            if (prefill != null) _vaultWindow.StartNew(prefill);
            else if (item != null) _vaultWindow.EditItem(item);
            _vaultWindow.Activate();
        }
    }

    private void Key_Up(object sender, EventArgs e)
    {
        _repeat.Stop();
        _repeating = null;
    }

    private void ToggleHeld(KeyDef def)
    {
        if (_held.Remove(def)) Send(def, up: true);
        else
        {
            _held.Add(def);
            Send(def, up: false);
        }
    }

    private void ReleaseHeld()
    {
        foreach (var def in _held) Send(def, up: true);
        _held.Clear();
    }

    private static void Tap(KeyDef def)
    {
        Send(def, up: false);
        Send(def, up: true);
    }

    private static void Send(KeyDef def, bool up)
    {
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (def.Vk != 0)
        {
            if (def.Extended) flags |= KEYEVENTF_EXTENDEDKEY;
            SendKey(def.Vk, 0, flags);
        }
        else
        {
            SendKey(0, def.Scan, flags | KEYEVENTF_SCANCODE);
        }
    }

    // ---- Language & display -----------------------------------------------------

    private void DetectLanguage()
    {
        int lang = GetForegroundPrimaryLanguage();
        if (lang == ArabicPrimaryLang) _lang = Lang.Arabic;
        else if (lang == EnglishPrimaryLang) _lang = Lang.English;
        // Any other layout: keep the last supported language.
    }

    private void Refresh()
    {
        bool shift = _held.Any(k => k.Kind == KeyKind.Shift);
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        LangButton.Content = _lang == Lang.Arabic ? "ع" : "EN";
        _dock?.SetLanguage((string)LangButton.Content);
        SaveWordButton.IsEnabled = _word.Length > 0;

        foreach (var (def, button) in _keys)
        {
            bool upper = shift ^ (caps && def.Letter && _lang == Lang.English);
            var label = def.Label(_lang, upper);
            if (button.Content as string != label) button.Content = label;

            bool active = _held.Contains(def) || (def.Kind == KeyKind.Caps && caps);
            var background = active ? ActiveBrush : def.IsModifier ? ModifierBrush : KeyBrush;
            if (!ReferenceEquals(button.Background, background)) button.Background = background;
            button.FontSize = label.Length > 2 ? _baseFont * 0.7 : _baseFont;
        }
    }

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        OptionsButton.IsChecked = false;
        // Win+Space cycles the installed input languages.
        SendKey(VK_LWIN, 0, 0);
        SendKey(VK_SPACE, 0, 0);
        SendKey(VK_SPACE, 0, KEYEVENTF_KEYUP);
        SendKey(VK_LWIN, 0, KEYEVENTF_KEYUP);
    }

    // ---- Options & chrome -------------------------------------------------------

    private void RestoreSettings()
    {
        if (_settings.Width is > 200 && _settings.Height is > 100)
        {
            Width = _settings.Width.Value;
            Height = _settings.Height.Value;
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (_settings.Left is { } left && _settings.Top is { } top
            && left < SystemParameters.VirtualScreenWidth - 50 && top < SystemParameters.VirtualScreenHeight - 50
            && left > -Width + 50 && top > -50)
        {
            Left = left;
            Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Bottom - Height - 10;
        }

        NumToggle.IsChecked = _settings.ShowNumberRow;
        FnToggle.IsChecked = _settings.ShowFunctionRow;
        NavToggle.IsChecked = _settings.ShowNavigationPanel;
        PinToggle.IsChecked = _settings.AlwaysOnTop;
        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.3, 1);
        PredictToggle.IsChecked = _settings.WordPredictions;
        LearnToggle.IsChecked = _settings.LearnWords;
        SpellToggle.IsChecked = _settings.SpellCheck;
        AutoCorrectToggle.IsChecked = _settings.AutoCorrect;
        DockToggle.IsChecked = _settings.DockOnMinimize;
        UpdateDockSideButtons();
        _voice = Shortcut.Parse(_settings.VoiceShortcut) ?? _voice;
        UpdateVoiceLabels();
        ApplyOptions();
    }

    private void ApplyOptions()
    {
        _numRow.Visibility = NumToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _fnRow.Visibility = FnToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _navRow.Visibility = NavToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (panel, def) in _rowDefs)
            def.Height = panel.Visibility == Visibility.Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        NavigationKeys.RowDefinitions[1].Height = _navRow.Visibility == Visibility.Visible ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        Topmost = PinToggle.IsChecked == true;
        AutoCorrectToggle.IsEnabled = SpellToggle.IsChecked == true;
        DockSidePanel.Visibility = DockToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateFontSize();
        ShowSuggestions();
    }

    private void KeyRows_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFontSize();

    private void UpdateFontSize()
    {
        int visible = _rowDefs.Keys.Count(p => p.Visibility == Visibility.Visible);
        double keyHeight = KeyRows.ActualHeight / Math.Max(visible, 1);
        double keyWidth = KeyRows.ActualWidth / 15;
        _baseFont = Math.Clamp(Math.Min(keyHeight, keyWidth) * 0.4, 8, 48);
        Refresh();
    }

    private void Options_Click(object sender, RoutedEventArgs e) => ApplyOptions();

    private void Opacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Opacity = e.NewValue;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not (Slider or Thumb)) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        AnimateOut(DockToggle.IsChecked == true ? Dock : () => WindowState = WindowState.Minimized);

    // ---- Docked compact mode ----------------------------------------------------

    private DockWindow? _dock;
    private Rect _dockArea;

    private void DockSide_Click(object sender, RoutedEventArgs e)
    {
        var side = (DockSide)((FrameworkElement)sender).Tag;
        // Picking a side starts the toolbar centred on it.
        if (side != _settings.DockSide) _settings.DockOffsets.Remove(side);
        _settings.DockSide = side;
        UpdateDockSideButtons();
    }

    private void UpdateDockSideButtons()
    {
        foreach (ToggleButton button in DockSidePanel.Children)
            button.IsChecked = (DockSide)button.Tag == _settings.DockSide;
    }

    /// <summary>Minimizes the keyboard and shows the compact toolbar on the chosen edge of its current monitor.</summary>
    private void Dock()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null)
        {
            WindowState = WindowState.Minimized;
            return;
        }
        var area = MonitorWorkArea(new WindowInteropHelper(this).Handle);
        area.Transform(source.CompositionTarget.TransformFromDevice);
        _dockArea = area;
        OptionsButton.IsChecked = false;
        ReleaseHeld();
        WindowState = WindowState.Minimized;

        var side = _settings.DockSide;
        var dock = new DockWindow(side, area, _settings.DockOffsets.GetValueOrDefault(side, 0.5))
        {
            Topmost = PinToggle.IsChecked == true,
            Opacity = OpacitySlider.Value,
        };
        dock.SetLanguage((string)LangButton.Content);
        dock.SetVoiceTooltip((string)VoiceButton.ToolTip);
        dock.RestoreRequested += () => WindowState = WindowState.Normal;
        dock.VoicePressed += PressVoice;
        dock.VoiceReleased += () => Voice_Up(dock, EventArgs.Empty);
        dock.LanguageRequested += () => Lang_Click(dock, new RoutedEventArgs());
        dock.VaultRequested += () => Vault_Click(dock, new RoutedEventArgs());
        var backspace = _keys.First(k => k.Def.Scan == 14).Def;
        dock.BackspacePressed += () => PressKey(backspace);
        dock.BackspaceReleased += () => Key_Up(dock, EventArgs.Empty);
        dock.OffsetChanged += offset => _settings.DockOffsets[side] = offset;
        dock.Closed += (_, _) => { if (_dock == dock) _dock = null; };
        _dock = dock;
        dock.Show();
    }

    private void Undock()
    {
        if (_dock is not { } dock) return;
        _dock = null;
        Voice_Up(this, EventArgs.Empty);
        Key_Up(this, EventArgs.Empty);
        // Keep an open vault picker alive: closing the toolbar would close the windows it owns.
        if (_vaultPicker != null && _vaultPicker.Owner == dock) _vaultPicker.Owner = this;
        dock.Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => AnimateOut(Close);

    // ---- Show/hide animation ----------------------------------------------------

    private const double SlideDistance = 30;
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private bool _animatingOut;

    /// <summary>Fade in while sliding up. Animations don't change the base values, so clearing them restores the real state.</summary>
    private void AnimateIn()
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var fade = new DoubleAnimation(0, Opacity, duration) { EasingFunction = EaseOut };
        // Release the held animations, otherwise they pin Top/Opacity and a dragged window snaps back
        // to its old position on minimize/close (and the opacity slider stops working).
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            BeginAnimation(TopProperty, null);
        };
        BeginAnimation(OpacityProperty, fade);
        BeginAnimation(TopProperty, new DoubleAnimation(Top + SlideDistance, Top, duration) { EasingFunction = EaseOut });
    }

    /// <summary>Fade out while sliding down, then run <paramref name="done"/> once the window is back to its normal state.</summary>
    private void AnimateOut(Action done)
    {
        if (_animatingOut) return;
        _animatingOut = true;
        var duration = TimeSpan.FromMilliseconds(160);
        var fade = new DoubleAnimation(Opacity, 0, duration) { EasingFunction = EaseIn };
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            BeginAnimation(TopProperty, null);
            _animatingOut = false;
            done();
        };
        BeginAnimation(OpacityProperty, fade);
        BeginAnimation(TopProperty, new DoubleAnimation(Top, Top + SlideDistance, duration) { EasingFunction = EaseIn });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Normal)
        {
            Undock(); // Restored from the toolbar or from the taskbar button.
            AnimateIn();
        }
        else SuggestionPopup.IsOpen = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        SuggestionPopup.IsOpen = false;
        Undock();
        _poll.Stop();
        _repeat.Stop();
        _saveTimer.Stop();
        Voice_Up(this, EventArgs.Empty);
        ReleaseHeld();
        _predictor.Save();
        _vaultPicker?.Close();
        _wordsWindow?.Close();
        _vaultWindow?.Close();

        _settings.ShowNumberRow = NumToggle.IsChecked == true;
        _settings.ShowFunctionRow = FnToggle.IsChecked == true;
        _settings.ShowNavigationPanel = NavToggle.IsChecked == true;
        _settings.AlwaysOnTop = PinToggle.IsChecked == true;
        _settings.Opacity = OpacitySlider.Value;
        _settings.WordPredictions = PredictToggle.IsChecked == true;
        _settings.LearnWords = LearnToggle.IsChecked == true;
        _settings.SpellCheck = SpellToggle.IsChecked == true;
        _settings.AutoCorrect = AutoCorrectToggle.IsChecked == true;
        _settings.DockOnMinimize = DockToggle.IsChecked == true;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Save();
        base.OnClosed(e);
    }
}

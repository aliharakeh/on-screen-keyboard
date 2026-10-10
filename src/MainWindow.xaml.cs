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
        _poll.Tick += (_, _) => { DetectLanguage(); Refresh(); };
        _poll.Start();
        DetectLanguage();
        Refresh();
        Loaded += (_, _) => AnimateIn();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        // Never take focus from the app the user is typing into.
        SetWindowLongPtr(source.Handle, GWL_EXSTYLE, GetWindowLongPtr(source.Handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
        source.AddHook(WndProc);
    }

    private static nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return MA_NOACTIVATE;
        }
        return 0;
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
        _navRow = AddRow(KeyboardLayouts.NavRow());
    }

    private Panel AddRow(List<KeyDef> defs)
    {
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
        KeyRows.RowDefinitions.Add(rowDef);
        Grid.SetRow(grid, KeyRows.RowDefinitions.Count - 1);
        KeyRows.Children.Add(grid);
        _rowDefs[grid] = rowDef;
        return grid;
    }

    // ---- Key handling -----------------------------------------------------------

    private void Key_Down(object sender, MouseButtonEventArgs e)
    {
        var def = (KeyDef)((Button)sender).Tag;
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

    // ---- Typed-word preview -----------------------------------------------------

    private string _word = "";

    private bool IsUpper(KeyDef def)
    {
        bool shift = _held.Any(k => k.Kind == KeyKind.Shift);
        bool caps = (GetKeyState(VK_CAPITAL) & 1) != 0;
        return shift ^ (caps && def.Letter && _lang == Lang.English);
    }

    /// <summary>Tracks the word being typed from on-screen key presses and shows it in the title bar.</summary>
    private void TrackWord(KeyDef def)
    {
        if (def.Vk != 0 || _held.Any(k => k.Kind is KeyKind.Ctrl or KeyKind.Alt or KeyKind.Win))
        {
            _word = ""; // Navigation/shortcuts move the caret somewhere we can't see.
        }
        else if (def.Kind == KeyKind.Caps)
        {
            return;
        }
        else if (def.Label(Lang.English, false) == "⌫")
        {
            if (_word.Length > 0) _word = _word[..^1];
        }
        else if (def.Letter || def.Label(Lang.English, false).Length == 1)
        {
            _word += def.Label(_lang, IsUpper(def));
        }
        else
        {
            _word = ""; // Space, Enter, Tab, Esc, F-keys end the word.
        }
        if (_word.Length > 40) _word = _word[^40..];
        WordText.Text = _word.Length > 0 ? _word : "On-Screen Keyboard";
        WordText.Foreground = _word.Length > 0 ? Brushes.White : Frozen("#FFAAAAAA");
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
        NavToggle.IsChecked = _settings.ShowNavRow;
        PinToggle.IsChecked = _settings.AlwaysOnTop;
        OpacitySlider.Value = Math.Clamp(_settings.Opacity, 0.3, 1);
        ApplyOptions();
    }

    private void ApplyOptions()
    {
        _numRow.Visibility = NumToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _fnRow.Visibility = FnToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _navRow.Visibility = NavToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (panel, def) in _rowDefs)
            def.Height = panel.Visibility == Visibility.Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Topmost = PinToggle.IsChecked == true;
        UpdateFontSize();
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
        AnimateOut(() => WindowState = WindowState.Minimized);

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
        if (WindowState == WindowState.Normal) AnimateIn();
    }

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        _repeat.Stop();
        ReleaseHeld();

        _settings.ShowNumberRow = NumToggle.IsChecked == true;
        _settings.ShowFunctionRow = FnToggle.IsChecked == true;
        _settings.ShowNavRow = NavToggle.IsChecked == true;
        _settings.AlwaysOnTop = PinToggle.IsChecked == true;
        _settings.Opacity = OpacitySlider.Value;
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Height = Height;
        _settings.Save();
        base.OnClosed(e);
    }
}

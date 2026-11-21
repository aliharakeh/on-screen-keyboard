using System.Windows;
using System.Windows.Input;

namespace OnScreenKeyboard;

/// <summary>Records the key combination the 🎤 button sends (Windows voice typing or an external app).</summary>
public partial class VoiceShortcutWindow : Window
{
    private Shortcut? _shortcut;
    private ModifierKeys _heldModifiers;
    private bool _keyPressed;

    public Shortcut? Result { get; private set; }

    public VoiceShortcutWindow(Shortcut? current)
    {
        InitializeComponent();
        Show(current);
        Loaded += (_, _) => ShortcutBox.Focus();
    }

    private void Show(Shortcut? shortcut)
    {
        _shortcut = shortcut;
        ShortcutBox.Text = shortcut?.ToString() ?? "";
        WinBox.IsChecked = shortcut?.Win == true;
        Status.Text = "";
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private Shortcut Build(ModifierKeys modifiers, Key key) => new(
        modifiers.HasFlag(ModifierKeys.Control), modifiers.HasFlag(ModifierKeys.Shift), modifiers.HasFlag(ModifierKeys.Alt),
        modifiers.HasFlag(ModifierKeys.Windows) || WinBox.IsChecked == true, key);

    private void ShortcutBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key switch { Key.System => e.SystemKey, Key.ImeProcessed => e.ImeProcessedKey, _ => e.Key };
        if (IsModifier(key))
        {
            if (_heldModifiers == ModifierKeys.None) _keyPressed = false;
            _heldModifiers |= Keyboard.Modifiers;
            ShortcutBox.Text = Build(_heldModifiers, Key.None) + "+…";
            return;
        }
        _keyPressed = true;
        Show(Build(Keyboard.Modifiers, key));
    }

    private void ShortcutBox_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        // Released only modifiers: record a modifier-only shortcut such as Ctrl+Win.
        if (!_keyPressed && _heldModifiers != ModifierKeys.None) Show(Build(_heldModifiers, Key.None));
        else Show(_shortcut);
        _heldModifiers = ModifierKeys.None;
        _keyPressed = false;
    }

    private void WinBox_Click(object sender, RoutedEventArgs e)
    {
        bool win = WinBox.IsChecked == true;
        var updated = (_shortcut ?? new Shortcut(false, false, false, false, Key.None)) with { Win = win };
        Show(updated.IsEmpty ? null : updated);
        WinBox.IsChecked = win;
        ShortcutBox.Focus();
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Show(Shortcut.Parse(Shortcut.WindowsVoiceTyping));

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcut == null)
        {
            Status.Text = "Press a shortcut first.";
            ShortcutBox.Focus();
            return;
        }
        Result = _shortcut;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

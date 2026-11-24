using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace OnScreenKeyboard;

/// <summary>
/// Compact toolbar shown flush against a screen edge while the keyboard is "minimized".
/// It never takes focus, and can be dragged along its edge by the grip.
/// </summary>
public partial class DockWindow : Window
{
    private const double Radius = 8;
    private readonly DockSide _side;
    private readonly Rect _area;
    private Point? _grab;

    public event Action? RestoreRequested;
    public event Action? VoicePressed;
    public event Action? VoiceReleased;
    public event Action? LanguageRequested;
    public event Action? VaultRequested;
    public event Action? BackspacePressed;
    public event Action? BackspaceReleased;
    /// <summary>Raised after a drag with the new position along the edge (0 = start, 1 = end).</summary>
    public event Action<double>? OffsetChanged;

    /// <param name="area">Work area of the target monitor, in WPF units.</param>
    /// <param name="offset">Position along the edge, 0 = start, 0.5 = centered, 1 = end.</param>
    public DockWindow(DockSide side, Rect area, double offset)
    {
        InitializeComponent();
        _side = side;
        _area = area;

        Bar.Orientation = Horizontal ? Orientation.Horizontal : Orientation.Vertical;
        GripText.Text = Horizontal ? "⋮⋮" : "⋯";
        // Square corners against the screen edge, rounded on the inside; no border on the edge side.
        (Frame.CornerRadius, Frame.BorderThickness) = side switch
        {
            DockSide.Left => (new CornerRadius(0, Radius, Radius, 0), new Thickness(0, 1, 1, 1)),
            DockSide.Top => (new CornerRadius(0, 0, Radius, Radius), new Thickness(1, 0, 1, 1)),
            DockSide.Right => (new CornerRadius(Radius, 0, 0, Radius), new Thickness(1, 1, 0, 1)),
            _ => (new CornerRadius(Radius, Radius, 0, 0), new Thickness(1, 1, 1, 0)),
        };

        Frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Frame.DesiredSize.Width;
        Height = Frame.DesiredSize.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;

        offset = Math.Clamp(offset, 0, 1);
        if (Horizontal)
        {
            Left = _area.Left + offset * (_area.Width - Width);
            Top = side == DockSide.Top ? _area.Top : _area.Bottom - Height;
        }
        else
        {
            Top = _area.Top + offset * (_area.Height - Height);
            Left = side == DockSide.Left ? _area.Left : _area.Right - Width;
        }
        Loaded += (_, _) => SlideIn();
    }

    private bool Horizontal => _side is DockSide.Top or DockSide.Bottom;

    public void SetLanguage(string label)
    {
        if (LangButton.Content as string != label) LangButton.Content = label;
    }

    public void SetVoiceTooltip(string tooltip) => VoiceButton.ToolTip = tooltip;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.MakeNoActivate(this);
    }

    /// <summary>Slides out from the screen edge while fading in.</summary>
    private void SlideIn()
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var (property, from) = _side switch
        {
            DockSide.Left => (LeftProperty, Left - Width),
            DockSide.Top => (TopProperty, Top - Height),
            DockSide.Right => (LeftProperty, Left + Width),
            _ => (TopProperty, Top + Height),
        };
        var slide = new DoubleAnimation(from, (double)GetValue(property), duration) { EasingFunction = ease };
        var fade = new DoubleAnimation(0, Opacity, duration) { EasingFunction = ease };
        // Release the animations so the base values (and dragging) take over again.
        fade.Completed += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            BeginAnimation(property, null);
        };
        BeginAnimation(OpacityProperty, fade);
        BeginAnimation(property, slide);
    }

    // ---- Dragging along the edge ----------------------------------------------

    private void Grip_Down(object sender, MouseButtonEventArgs e)
    {
        _grab = e.GetPosition(this);
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void Grip_Move(object sender, MouseEventArgs e)
    {
        if (_grab is not { } grab) return;
        var p = e.GetPosition(this);
        if (Horizontal) Left = Math.Clamp(Left + p.X - grab.X, _area.Left, _area.Right - Width);
        else Top = Math.Clamp(Top + p.Y - grab.Y, _area.Top, _area.Bottom - Height);
    }

    private void Grip_Up(object sender, MouseButtonEventArgs e) => Grip.ReleaseMouseCapture();

    private void Grip_Lost(object sender, MouseEventArgs e)
    {
        if (_grab == null) return;
        _grab = null;
        double travel = Horizontal ? _area.Width - Width : _area.Height - Height;
        double moved = Horizontal ? Left - _area.Left : Top - _area.Top;
        OffsetChanged?.Invoke(travel > 0 ? Math.Clamp(moved / travel, 0, 1) : 0.5);
    }

    // ---- Buttons ----------------------------------------------------------------

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();
    private void Voice_Down(object sender, MouseButtonEventArgs e) => VoicePressed?.Invoke();
    private void Voice_Up(object sender, EventArgs e) => VoiceReleased?.Invoke();
    private void Lang_Click(object sender, RoutedEventArgs e) => LanguageRequested?.Invoke();
    private void Vault_Click(object sender, RoutedEventArgs e) => VaultRequested?.Invoke();
    private void Backspace_Down(object sender, MouseButtonEventArgs e) => BackspacePressed?.Invoke();
    private void Backspace_Up(object sender, EventArgs e) => BackspaceReleased?.Invoke();
}

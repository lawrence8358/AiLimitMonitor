using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Tray;

/// <summary>
/// Borderless dark popup shown near the tray icon; auto-hides once the mouse
/// moves away from both the popup and the spot it was opened from. A little pet
/// runs along the text border unless disabled from the tray menu.
/// </summary>
internal sealed class UsagePopupForm : Form
{
    private readonly Label _label;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly System.Windows.Forms.Timer _petTimer;
    private Point _anchor;
    private string _baseText = "";
    private long _petTick;
    private bool _petEnabled = true;

    public UsagePopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(30, 30, 30);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // The label sits at (16, 14); mirror that gap on the right/bottom via Padding
        // so the auto-sized form keeps an even margin all around.
        Padding = new Padding(0, 0, 16, 14);

        _label = new Label
        {
            AutoSize = true,
            Location = new Point(16, 14),
            Font = new Font("Consolas", 10f),
            ForeColor = Color.Gainsboro,
            BackColor = Color.Transparent,
        };
        Controls.Add(_label);

        _hideTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _hideTimer.Tick += (_, _) => HideIfMouseAway();

        _petTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _petTimer.Tick += (_, _) =>
        {
            _petTick++;
            RefreshLabel();
        };
    }

    /// <summary>Toggled from the tray context menu.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool PetEnabled
    {
        get => _petEnabled;
        set
        {
            _petEnabled = value;
            _petTimer.Enabled = value && Visible;
            RefreshLabel();
        }
    }

    // Never steal focus when shown.
    protected override bool ShowWithoutActivation => true;

    public void ShowNear(Point cursor, string text)
    {
        _anchor = cursor;
        UpdateText(text);

        var screen = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Min(cursor.X, screen.Right - Width - 8);
        var y = cursor.Y - Height - 12 < screen.Top ? cursor.Y + 16 : cursor.Y - Height - 12;
        Location = new Point(Math.Max(screen.Left + 8, x), y);

        if (!Visible)
            Show();
        _hideTimer.Start();
        if (_petEnabled)
            _petTimer.Start();
    }

    public void UpdateText(string text)
    {
        _baseText = text.TrimEnd();
        RefreshLabel();
    }

    // The border is always drawn so toggling the pet never shifts the layout.
    private void RefreshLabel() =>
        _label.Text = _baseText.Length > 0
            ? PetBorder.Wrap(_baseText, _petTick, showPet: _petEnabled).TrimEnd()
            : _baseText;

    private void HideIfMouseAway()
    {
        var mouse = Cursor.Position;
        var nearPopup = Inflate(Bounds, 24).Contains(mouse);
        var nearAnchor = Math.Abs(mouse.X - _anchor.X) < 48 && Math.Abs(mouse.Y - _anchor.Y) < 48;
        if (!nearPopup && !nearAnchor)
        {
            _hideTimer.Stop();
            _petTimer.Stop();
            Hide();
        }
    }

    private static Rectangle Inflate(Rectangle rect, int amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _petTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}

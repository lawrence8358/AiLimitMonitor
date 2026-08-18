using System.Globalization;
using AiLimitMonitor.Core.Config;

namespace AiLimitMonitor.Tray;

/// <summary>Edits the global time ranges in which keep-alive calls are allowed.</summary>
internal sealed class KeepAliveScheduleForm : Form
{
    private static readonly (DayOfWeek Day, string Text)[] OrderedDays =
    [
        (DayOfWeek.Monday, "一"),
        (DayOfWeek.Tuesday, "二"),
        (DayOfWeek.Wednesday, "三"),
        (DayOfWeek.Thursday, "四"),
        (DayOfWeek.Friday, "五"),
        (DayOfWeek.Saturday, "六"),
        (DayOfWeek.Sunday, "日"),
    ];

    private readonly RadioButton _unrestrictedButton;
    private readonly RadioButton _restrictedButton;
    private readonly DataGridView _ruleGrid;
    private readonly Button _addButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly List<KeepAliveScheduleRuleConfig> _rules;

    public KeepAliveScheduleForm(KeepAliveScheduleConfig? current)
    {
        Text = "自動 hello 允許呼叫時段";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(610, 430);
        Font = new Font("Microsoft JhengHei UI", 9f);

        _rules = current?.Rules.Select(CloneRule).ToList() ?? [];

        var description = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(570, 0),
            Text = "只限制自動 hello；使用量監控仍會持續執行。時間依 Windows 本機時區判斷。",
            Location = new Point(20, 18),
        };

        _unrestrictedButton = new RadioButton
        {
            AutoSize = true,
            Text = "不限時段（全天允許）",
            Location = new Point(22, 57),
            Checked = current is null,
        };
        _restrictedButton = new RadioButton
        {
            AutoSize = true,
            Text = "僅限以下時段",
            Location = new Point(22, 85),
            Checked = current is not null,
        };

        _ruleGrid = new DataGridView
        {
            Location = new Point(22, 118),
            Size = new Size(566, 220),
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            MultiSelect = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        _ruleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "星期",
            Width = 310,
        });
        _ruleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "開始",
            Width = 105,
        });
        _ruleGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "結束",
            Width = 105,
        });
        _ruleGrid.SelectionChanged += (_, _) => UpdateEnabledState();
        _ruleGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                EditSelectedRule();
        };

        _addButton = new Button { Text = "新增…", Location = new Point(22, 350), Size = new Size(90, 32) };
        _editButton = new Button { Text = "編輯…", Location = new Point(120, 350), Size = new Size(90, 32) };
        _deleteButton = new Button { Text = "刪除", Location = new Point(218, 350), Size = new Size(90, 32) };
        _addButton.Click += (_, _) => AddRule();
        _editButton.Click += (_, _) => EditSelectedRule();
        _deleteButton.Click += (_, _) => DeleteSelectedRule();

        var saveButton = new Button
        {
            Text = "儲存",
            Location = new Point(400, 378),
            Size = new Size(90, 34),
        };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(498, 378),
            Size = new Size(90, 34),
        };
        saveButton.Click += (_, _) => SaveAndClose();

        _unrestrictedButton.CheckedChanged += (_, _) => UpdateEnabledState();
        _restrictedButton.CheckedChanged += (_, _) => UpdateEnabledState();

        Controls.AddRange([
            description,
            _unrestrictedButton,
            _restrictedButton,
            _ruleGrid,
            _addButton,
            _editButton,
            _deleteButton,
            saveButton,
            cancelButton,
        ]);
        AcceptButton = saveButton;
        CancelButton = cancelButton;

        RefreshGrid();
        UpdateEnabledState();
    }

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public KeepAliveScheduleConfig? SelectedSchedule { get; private set; }

    private void AddRule()
    {
        using var editor = new KeepAliveScheduleRuleForm(null);
        if (ShowRuleEditor(editor) != DialogResult.OK)
            return;
        _rules.Add(editor.SelectedRule);
        RefreshGrid(_rules.Count - 1);
    }

    private void EditSelectedRule()
    {
        var index = SelectedIndex();
        if (index < 0)
            return;

        using var editor = new KeepAliveScheduleRuleForm(_rules[index]);
        if (ShowRuleEditor(editor) != DialogResult.OK)
            return;
        _rules[index] = editor.SelectedRule;
        RefreshGrid(index);
    }

    private DialogResult ShowRuleEditor(KeepAliveScheduleRuleForm editor)
    {
        // A modal form does not automatically inherit its owner's TopMost Win32 band.
        // Keep both windows in the same band so the disabled owner cannot cover the editor.
        editor.TopMost = TopMost;
        return editor.ShowDialog(this);
    }

    private void DeleteSelectedRule()
    {
        var index = SelectedIndex();
        if (index < 0)
            return;
        _rules.RemoveAt(index);
        RefreshGrid(Math.Min(index, _rules.Count - 1));
    }

    private int SelectedIndex() =>
        _ruleGrid.SelectedRows.Count == 1 ? _ruleGrid.SelectedRows[0].Index : -1;

    private void RefreshGrid(int selectedIndex = 0)
    {
        _ruleGrid.Rows.Clear();
        foreach (var rule in _rules)
            _ruleGrid.Rows.Add(FormatDays(rule.Days), rule.Start, rule.End);

        if (selectedIndex >= 0 && selectedIndex < _ruleGrid.Rows.Count)
            _ruleGrid.Rows[selectedIndex].Selected = true;
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        var restricted = _restrictedButton.Checked;
        _ruleGrid.Enabled = restricted;
        _addButton.Enabled = restricted;
        _editButton.Enabled = restricted && SelectedIndex() >= 0;
        _deleteButton.Enabled = restricted && SelectedIndex() >= 0;
    }

    private void SaveAndClose()
    {
        if (_restrictedButton.Checked)
        {
            if (_rules.Count == 0)
            {
                MessageBox.Show(this, "限制呼叫時段時，請至少新增一個時段。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_rules.Any(rule => !IsValid(rule)))
            {
                MessageBox.Show(this, "設定中有無效的星期或時間，請編輯後再儲存。", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SelectedSchedule = new KeepAliveScheduleConfig
            {
                Rules = _rules.Select(CloneRule).ToList(),
            };
        }
        else
        {
            SelectedSchedule = null;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsValid(KeepAliveScheduleRuleConfig rule) =>
        rule.Days.Count > 0 &&
        TryParseTime(rule.Start, out var start) &&
        TryParseTime(rule.End, out var end) &&
        start != end;

    private static bool TryParseTime(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);

    private static KeepAliveScheduleRuleConfig CloneRule(KeepAliveScheduleRuleConfig rule) => new()
    {
        Days = [.. rule.Days],
        Start = rule.Start,
        End = rule.End,
    };

    private static string FormatDays(IEnumerable<DayOfWeek> days)
    {
        var selected = days.ToHashSet();
        if (OrderedDays.All(item => selected.Contains(item.Day)))
            return "每天";
        if (OrderedDays.Take(5).All(item => selected.Contains(item.Day)) &&
            OrderedDays.Skip(5).All(item => !selected.Contains(item.Day)))
            return "星期一～五";
        return string.Join("、", OrderedDays.Where(item => selected.Contains(item.Day))
            .Select(item => $"星期{item.Text}"));
    }
}

/// <summary>Edits one weekday/time range. Overnight ranges are intentionally accepted.</summary>
internal sealed class KeepAliveScheduleRuleForm : Form
{
    private static readonly (DayOfWeek Day, string Text)[] OrderedDays =
    [
        (DayOfWeek.Monday, "一"),
        (DayOfWeek.Tuesday, "二"),
        (DayOfWeek.Wednesday, "三"),
        (DayOfWeek.Thursday, "四"),
        (DayOfWeek.Friday, "五"),
        (DayOfWeek.Saturday, "六"),
        (DayOfWeek.Sunday, "日"),
    ];

    private readonly CheckBox[] _dayBoxes;
    private readonly DateTimePicker _startPicker;
    private readonly DateTimePicker _endPicker;

    public KeepAliveScheduleRuleForm(KeepAliveScheduleRuleConfig? current)
    {
        Text = current is null ? "新增允許呼叫時段" : "編輯允許呼叫時段";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(500, 235);
        Font = new Font("Microsoft JhengHei UI", 9f);

        Controls.Add(new Label { Text = "星期", AutoSize = true, Location = new Point(22, 24) });
        _dayBoxes = new CheckBox[OrderedDays.Length];
        for (var i = 0; i < OrderedDays.Length; i++)
        {
            var item = OrderedDays[i];
            var box = new CheckBox
            {
                AutoSize = true,
                Text = item.Text,
                Tag = item.Day,
                Location = new Point(78 + i * 56, 20),
                Checked = current?.Days.Contains(item.Day) ?? i < 5,
            };
            _dayBoxes[i] = box;
            Controls.Add(box);
        }

        Controls.Add(new Label { Text = "開始時間", AutoSize = true, Location = new Point(22, 78) });
        _startPicker = CreateTimePicker(new Point(105, 72), current?.Start, new TimeOnly(9, 0));
        Controls.Add(_startPicker);

        Controls.Add(new Label { Text = "結束時間", AutoSize = true, Location = new Point(258, 78) });
        _endPicker = CreateTimePicker(new Point(341, 72), current?.End, new TimeOnly(18, 0));
        Controls.Add(_endPicker);

        Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(22, 120),
            Text = "結束時間早於開始時間時視為跨日；星期以開始日為準。",
        });

        var okButton = new Button { Text = "確定", Location = new Point(302, 178), Size = new Size(82, 34) };
        var cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(394, 178),
            Size = new Size(82, 34),
        };
        okButton.Click += (_, _) => SaveAndClose();
        Controls.Add(okButton);
        Controls.Add(cancelButton);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        SelectedRule = current is null ? new KeepAliveScheduleRuleConfig() : CloneRule(current);
    }

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public KeepAliveScheduleRuleConfig SelectedRule { get; private set; }

    private void SaveAndClose()
    {
        var days = _dayBoxes.Where(box => box.Checked).Select(box => (DayOfWeek)box.Tag!).ToList();
        if (days.Count == 0)
        {
            MessageBox.Show(this, "請至少選擇一個星期。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var start = TimeOnly.FromDateTime(_startPicker.Value);
        var end = TimeOnly.FromDateTime(_endPicker.Value);
        if (start == end)
        {
            MessageBox.Show(this, "開始與結束時間不可相同。", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SelectedRule = new KeepAliveScheduleRuleConfig
        {
            Days = days,
            Start = start.ToString("HH:mm", CultureInfo.InvariantCulture),
            End = end.ToString("HH:mm", CultureInfo.InvariantCulture),
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static DateTimePicker CreateTimePicker(Point location, string? value, TimeOnly fallback)
    {
        var time = value is not null &&
                   TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                       DateTimeStyles.None, out var parsed)
            ? parsed
            : fallback;
        return new DateTimePicker
        {
            CustomFormat = "HH:mm",
            Format = DateTimePickerFormat.Custom,
            ShowUpDown = true,
            Location = location,
            Size = new Size(118, 28),
            Value = DateTime.Today.Add(time.ToTimeSpan()),
        };
    }

    private static KeepAliveScheduleRuleConfig CloneRule(KeepAliveScheduleRuleConfig rule) => new()
    {
        Days = [.. rule.Days],
        Start = rule.Start,
        End = rule.End,
    };
}

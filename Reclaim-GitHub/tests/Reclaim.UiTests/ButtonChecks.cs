using System.Reflection;
using System.Runtime.InteropServices;
using Reclaim.App;

internal static partial class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    private static Control NewButton(string caption, bool primary = false, bool dark = false, bool selected = false)
    {
        Type type = typeof(MainForm).Assembly.GetType("Reclaim.App.ActionButton")!;
        var button = (Control)Activator.CreateInstance(type, [caption, primary])!;
        type.GetProperty("Dark")!.SetValue(button, dark);
        type.GetProperty("Selected")!.SetValue(button, selected);
        return button;
    }
    private static object? ButtonEvent(Control button, string method, object value) =>
        button.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(button, [value]);

    private static void ButtonChecks()
    {
        Directory.CreateDirectory("artifacts");
        using var window = new Form { Opacity = 0, ShowInTaskbar = false, ClientSize = new Size(300, 100), BackColor = Color.White };
        // Exercise a real HWND lifecycle, including a transparent table between button and card.
        var transparent = new TableLayoutPanel { BackColor = Color.Transparent, Dock = DockStyle.Fill };
        window.Controls.Add(transparent); window.Show(); Application.DoEvents();
        using var gallery = new Bitmap(1180, 440);
        using var graphics = Graphics.FromImage(gallery); graphics.Clear(Color.FromArgb(244, 246, 248));
        string[] states = ["Normal", "Hover", "Pressed", "Disabled", "Keyboard focus"];
        using var heading = new Font("Segoe UI", 11, FontStyle.Bold);
        using var labelFont = new Font("Segoe UI", 10);
        for (int i = 0; i < states.Length; i++) graphics.DrawString(states[i], heading, Brushes.DimGray, 185 + i * 194, 18);
        string[] styles = ["Primary action", "Secondary action", "Active preview tab", "Sidebar action"];
        int renders = 0;
        for (int row = 0; row < styles.Length; row++)
        {
            graphics.DrawString(styles[row], labelFont, Brushes.DimGray, 16, 85 + row * 86);
            for (int col = 0; col < states.Length; col++)
            {
                window.BackColor = row == 3 ? Color.FromArgb(18, 34, 47) : Color.White;
                using var button = NewButton(row == 0 ? "Start scan" : row == 1 ? "Choose folder" : row == 2 ? "Preview" : "Recovery guide", row == 0, row == 3, row == 2);
                button.AutoSize = false; button.Size = new Size(184, 50); transparent.Controls.Add(button);
                _ = button.Handle; Application.DoEvents();
                if (col is 1 or 2) ButtonEvent(button, "OnMouseEnter", EventArgs.Empty);
                if (col == 2) ButtonEvent(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 30, 20, 0));
                if (col == 3) button.Enabled = false;
                if (col == 4) { button.Focus(); ((IButtonControl)button).NotifyDefault(true); }
                using var own = new Bitmap(button.Width, button.Height);
                button.DrawToBitmap(own, new Rectangle(Point.Empty, own.Size));
                foreach (Point corner in new[] { Point.Empty, new Point(own.Width - 1, 0), new Point(0, own.Height - 1), new Point(own.Width - 1, own.Height - 1) })
                    if (own.GetPixel(corner.X, corner.Y).ToArgb() != window.BackColor.ToArgb()) throw new Exception($"{styles[row]}, {states[col]}: corner did not match the opaque parent surface.");
                // The entire outer edge must be free of the old black native border remnants.
                for (int x = 0; x < own.Width; x++)
                    foreach (int y in new[] { 0, own.Height - 1 })
                    {
                        var pixel = own.GetPixel(x, y);
                        if (pixel.R < 8 && pixel.G < 8 && pixel.B < 8) throw new Exception("Black button edge survived repainting.");
                    }
                graphics.DrawImageUnscaled(own, 178 + col * 194, 70 + row * 86); renders++;
                button.Capture = false; transparent.Controls.Remove(button);
            }
        }
        gallery.Save("artifacts/reclaim-button-states.png");
        Check(renders == 20, "20 native-window button renders have clean corners across light/dark and interaction states");

        using var action = NewButton("Test action"); transparent.Controls.Add(action);
        int clicks = 0; action.Click += (_, _) => clicks++;
        var input = (IButtonControl)action;
        input.PerformClick(); Check(clicks == 1, "Programmatic button activation fires once");
        action.Enabled = false; input.PerformClick(); Check(clicks == 1, "Disabled buttons reject activation"); action.Enabled = true;
        ButtonEvent(action, "OnKeyDown", new KeyEventArgs(Keys.Space)); ButtonEvent(action, "OnKeyDown", new KeyEventArgs(Keys.Space));
        ButtonEvent(action, "OnKeyUp", new KeyEventArgs(Keys.Space)); Check(clicks == 2, "Holding Space activates once when released");
        ButtonEvent(action, "ProcessDialogKey", Keys.Enter); Check(clicks == 3, "Enter activates the focused button");
        ButtonEvent(action, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 15, 15, 0));
        ButtonEvent(action, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 15, 15, 0)); Check(clicks == 4, "A mouse click activates once");
        ButtonEvent(action, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 15, 15, 0));
        ButtonEvent(action, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, -5, -5, 0)); Check(clicks == 4, "Dragging outside cancels activation");
        action.AccessibilityObject.DoDefaultAction(); Check(clicks == 5, "The accessibility default action activates the button");
        input.DialogResult = DialogResult.OK; window.AcceptButton = input; window.CancelButton = input;
        input.PerformClick(); Check(window.DialogResult == DialogResult.OK && clicks == 6, "Dialog buttons retain their result and accept/cancel contracts");
        window.Close();

        using var form = new MainForm { Opacity = 0, ShowInTaskbar = false, WindowState = FormWindowState.Maximized };
        form.Show(); Application.DoEvents(); form.Refresh();
        AssertAlignment(form);
        using var preview = NewFileViewer();
        var tabs = Descendants(preview).Where(x => x.GetType().Name == "ActionButton" && x.Text is "Preview" or "Details" or "Hex").ToArray();
        bool IsSelected(Control button) => (bool)button.GetType().GetProperty("Selected")!.GetValue(button)!;
        Check(IsSelected(tabs.Single(x => x.Text == "Preview")), "Preview is highlighted before any file is selected");
        ((IButtonControl)tabs.Single(x => x.Text == "Details")).PerformClick(); WaitPreview(preview);
        Check(IsSelected(tabs.Single(x => x.Text == "Details")) && !IsSelected(tabs.Single(x => x.Text == "Preview")), "Clicking a preview tab moves its selected appearance");
        ((IButtonControl)tabs.Single(x => x.Text == "Preview")).PerformClick(); WaitPreview(preview);
        string sample = "artifacts/tests/sample-ntfs.img";
        if (File.Exists(sample)) { form.LoadPreview(Reclaim.Core.RecoveryEngine.Scan(new(sample, true), Reclaim.Core.ScanMode.Deep, null, CancellationToken.None)); WaitPreview(Field<Control>(form, "gallery")); }
        using var live = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        using (var canvas = Graphics.FromImage(live))
        {
            IntPtr dc = canvas.GetHdc();
            try { Check(PrintWindow(form.Handle, dc, 3), "The actual maximized window paints successfully through Windows"); }
            finally { canvas.ReleaseHdc(dc); }
        }
        live.Save("artifacts/reclaim-buttons-live.png");
        form.Close();
        Console.WriteLine("12 button checks passed. Native test windows were invisible and closed; no drive was scanned.");
    }
}

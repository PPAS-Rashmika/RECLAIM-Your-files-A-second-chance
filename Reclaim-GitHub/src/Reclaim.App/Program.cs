using Reclaim.Core;

namespace Reclaim.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? source = Argument(args, "--image") ?? Argument(args, "--source");
        using var form = new MainForm(source, args.Contains("--image"));
        if (Argument(args, "--render-preview") is string output)
        {
            form.FormBorderStyle = FormBorderStyle.None;
            _ = form.Handle; form.PerformLayout();
            if (Argument(args, "--image") is string image)
                form.LoadPreview(RecoveryEngine.Scan(new ScanSource(image, true), ScanMode.Deep, null, CancellationToken.None));
            _ = form.Handle;
            form.PerformLayout();
            if (Argument(args, "--preview-name") is string name) form.SelectPreviewFile(name);
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!form.PreviewPending.IsCompleted && wait.Elapsed.TotalSeconds < 10) { Application.DoEvents(); Thread.Sleep(10); }
            using var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            RenderControls(form, bitmap, Point.Empty);
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return;
        }
        Application.Run(form);
    }
    private static void RenderControls(Control control, Bitmap bitmap, Point offset)
    {
        control.CreateControl(); control.PerformLayout();
        if (control.Width > 0 && control.Height > 0)
        {
            using var own = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(own, new Rectangle(Point.Empty, own.Size));
            using var graphics = Graphics.FromImage(bitmap); graphics.DrawImageUnscaled(own, offset);
            if (control is ComboBox combo)
                TextRenderer.DrawText(graphics, combo.Text, combo.Font, new Rectangle(offset.X + 5, offset.Y + 2, Math.Max(0, combo.Width - 25), combo.Height - 4), combo.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (control is TextBox text && text.Text.Length == 0 && text.PlaceholderText.Length > 0)
                TextRenderer.DrawText(graphics, text.PlaceholderText, text.Font, new Rectangle(offset.X + 4, offset.Y + 2, text.Width - 8, text.Height - 4), SystemColors.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        foreach (Control child in control.Controls.Cast<Control>().Reverse())
        {
            if (child.Tag is string tag && tag == "SkipPreview") continue;
            RenderControls(child, bitmap, new Point(offset.X + child.Left, offset.Y + child.Top));
        }
    }
    private static string? Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

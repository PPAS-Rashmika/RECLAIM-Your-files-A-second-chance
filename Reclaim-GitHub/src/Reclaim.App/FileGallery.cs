using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Reclaim.Core;

namespace Reclaim.App;

// A virtual gallery: paint only visible cards and decode thumbnails one at a time.
internal sealed class FileGallery : Control
{
    private sealed class TilePreview(Bitmap? image, string text, string kind) : IDisposable
    {
        public Bitmap? Image { get; } = image;
        public string Text { get; } = text;
        public string Kind { get; } = kind;
        public void Dispose() => Image?.Dispose();
    }
    private readonly Dictionary<Guid, TilePreview> cache = [];
    private readonly LinkedList<Guid> recent = [];
    private readonly ToolTip tooltip = new();
    private readonly VScrollBar scroll = new() { Dock = DockStyle.Right, TabStop = false };
    private IReadOnlyList<RecoveryItem> items = [];
    private ScanResult? source;
    private Func<Guid, bool> isChecked = _ => false;
    private int current = -1, generation, columns = 1, cardWidth, hovered = -1;
    private bool loading, selectionEnabled = true, disposed, spacePressed;
    private readonly Font nameFont = Theme.Font(10, FontStyle.Bold), bodyFont = Theme.Font(9), smallFont = Theme.Font(8), badgeFont = Theme.Font(18, FontStyle.Bold);
    public event EventHandler? CurrentItemChanged;
    public event Action<RecoveryItem, bool>? CheckRequested;
    public event EventHandler? ItemActivated;
    public int ItemCount => items.Count;
    public RecoveryItem? CurrentItem => current >= 0 && current < items.Count ? items[current] : null;
    public bool HasImage => CurrentItem is { } item && cache.TryGetValue(item.Id, out var value) && value.Image is not null;
    public string PreviewBody => CurrentItem is { } item && cache.TryGetValue(item.Id, out var value) ? value.Text : "";
    public int CachedCount => cache.Count;
    public Task Pending { get; private set; } = Task.CompletedTask;
    private int Gap => LogicalToDeviceUnits(12);
    private int CardHeight => LogicalToDeviceUnits(276);

    public FileGallery()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true; BackColor = Theme.Background; Margin = Padding.Empty;
        Controls.Add(scroll); scroll.ValueChanged += (_, _) => { Invalidate(); StartLoading(); };
        AccessibleRole = AccessibleRole.List; AccessibleName = "Recovered files with inline previews";
        AccessibleDescription = "Arrow keys move between files. Space selects a file for recovery. Enter opens the file viewer.";
    }
    public void SetItems(ScanResult? scan, IReadOnlyList<RecoveryItem> visible, Func<Guid, bool> checkedState, bool preservePosition = false)
    {
        Guid? previous = CurrentItem?.Id;
        if (!ReferenceEquals(source, scan))
        {
            generation++; source = scan;
            foreach (var value in cache.Values) value.Dispose();
            cache.Clear(); recent.Clear();
        }
        items = visible; isChecked = checkedState;
        current = previous is Guid id ? items.ToList().FindIndex(x => x.Id == id) : -1;
        if (current < 0 && items.Count > 0) current = 0;
        hovered = -1; tooltip.SetToolTip(this, null); if (!preservePosition) scroll.Value = 0;
        UpdateGeometry(); Invalidate(); StartLoading(); CurrentItemChanged?.Invoke(this, EventArgs.Empty);
    }
    public void SetSelectionEnabled(bool value) { selectionEnabled = value; Invalidate(); }
    public void FocusItem(string name)
    {
        int index = items.ToList().FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) SetCurrent(index);
    }
    public void ToggleCurrent()
    {
        if (selectionEnabled && CurrentItem is { CanRecover: true } item) CheckRequested?.Invoke(item, !isChecked(item.Id));
    }
    private Rectangle CardBounds(int index) => new(index % columns * (cardWidth + Gap), index / columns * (CardHeight + Gap) - scroll.Value, cardWidth, CardHeight);
    private Rectangle CheckBounds(Rectangle card) => new(card.X + LogicalToDeviceUnits(16), card.Y + LogicalToDeviceUnits(16), LogicalToDeviceUnits(22), LogicalToDeviceUnits(22));
    private void UpdateGeometry()
    {
        if (scroll is null) return;
        scroll.Width = LogicalToDeviceUnits(17);
        int available = Math.Max(1, ClientSize.Width - scroll.Width);
        columns = Math.Max(1, (available + Gap) / LogicalToDeviceUnits(244));
        cardWidth = Math.Max(1, (available - (columns - 1) * Gap) / columns);
        int rows = (items.Count + columns - 1) / columns;
        int extent = rows == 0 ? 0 : rows * (CardHeight + Gap) - Gap;
        scroll.LargeChange = Math.Max(1, ClientSize.Height);
        scroll.SmallChange = LogicalToDeviceUnits(60);
        scroll.Maximum = Math.Max(0, extent - 1);
        scroll.Visible = extent > ClientSize.Height;
        scroll.Value = Math.Min(scroll.Value, Math.Max(0, extent - ClientSize.Height));
    }
    private IEnumerable<int> VisibleIndices()
    {
        if (items.Count == 0) yield break;
        int first = Math.Max(0, scroll.Value / (CardHeight + Gap) * columns);
        int last = Math.Min(items.Count, ((scroll.Value + ClientSize.Height) / (CardHeight + Gap) + 1) * columns);
        for (int index = first; index < last; index++) yield return index;
    }
    protected override void OnLayout(LayoutEventArgs levent) { base.OnLayout(levent); UpdateGeometry(); StartLoading(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int lines = SystemInformation.MouseWheelScrollLines;
        int step = lines < 0 ? scroll.LargeChange : scroll.SmallChange * Math.Max(1, lines);
        scroll.Value = Math.Clamp(scroll.Value - e.Delta * step / 120, 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
    }
    private void StartLoading()
    {
        if (loading || disposed || source is null || items.Count == 0 || Width < 20 || Height < 20) return;
        if (!VisibleIndices().Any(x => !cache.ContainsKey(items[x].Id))) return;
        loading = true; Pending = LoadVisible();
    }
    private async Task LoadVisible()
    {
        try
        {
            while (!disposed && source is not null)
            {
                var item = VisibleIndices().Select(x => items[x]).FirstOrDefault(x => !cache.ContainsKey(x.Id));
                if (item is null) break;
                int revision = generation; var scan = source;
                var value = await Task.Run(() => Decode(scan, item));
                if (disposed || revision != generation) { value.Dispose(); continue; }
                cache[item.Id] = value; recent.Remove(item.Id); recent.AddLast(item.Id);
                var visible = VisibleIndices().Select(x => items[x].Id).ToHashSet();
                while (cache.Count > Math.Max(80, visible.Count) && recent.First is { } first)
                {
                    var id = first.Value; recent.RemoveFirst();
                    if (visible.Contains(id)) { recent.AddLast(id); continue; }
                    cache[id].Dispose(); cache.Remove(id);
                }
                Invalidate();
            }
        }
        finally { loading = false; }
    }
    private static TilePreview Decode(ScanResult scan, RecoveryItem item)
    {
        string extension = Path.GetExtension(item.Name).ToLowerInvariant();
        string badge = extension.Length > 1 ? extension[1..].ToUpperInvariant() : "FILE";
        if (!item.CanRecover) return new(null, "File data unavailable", badge);
        try
        {
            if (new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico" }.Contains(extension))
            {
                if (item.Size > 32 * 1024 * 1024) return new(null, "Image exceeds thumbnail limit", badge);
                using var stream = RecoveryContent.Open(scan, item); using var decoded = Image.FromStream(stream, true, true);
                if ((long)decoded.Width * decoded.Height > 25_000_000) return new(null, "Image exceeds thumbnail limit", badge);
                double scale = Math.Min(1, Math.Min(640d / decoded.Width, 400d / decoded.Height));
                var bitmap = new Bitmap(Math.Max(1, (int)(decoded.Width * scale)), Math.Max(1, (int)(decoded.Height * scale)), PixelFormat.Format32bppPArgb);
                try
                {
                    using var g = Graphics.FromImage(bitmap); g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.White); g.DrawImage(decoded, new Rectangle(Point.Empty, bitmap.Size));
                    return new(bitmap, $"{decoded.Width:N0} × {decoded.Height:N0} px", badge);
                }
                catch { bitmap.Dispose(); throw; }
            }
            if (PreviewText.IsText(extension) || new[] { ".docx", ".xlsx", ".pptx", ".epub", ".odt", ".ods", ".odp", ".zip" }.Contains(extension))
            {
                if (item.Size > 32 * 1024 * 1024) return new(null, "Open to inspect this large file", badge);
                string text = PreviewText.Read(scan, item);
                if (!PreviewText.IsText(extension)) { int start = text.IndexOf("\r\n\r\n", StringComparison.Ordinal); if (start >= 0) text = text[(start + 4)..]; }
                string snippet = text[..Math.Min(1600, text.Length)].Trim();
                return new(null, snippet.Length == 0 ? "Empty text file" : snippet, "TEXT:" + badge);
            }
            return new(null, extension == ".pdf" ? "PDF document · Enter to open" : item.Category is FileCategory.Audio or FileCategory.Videos ? "Media file · Enter to open" : "Details and byte preview available", badge);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Xml.XmlException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { return new(null, "Preview unavailable · inspect before recovering", badge); }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (int index in VisibleIndices()) PaintCard(g, index);
        StartLoading();
    }
    private void PaintCard(Graphics g, int index)
    {
        if (cardWidth < 8) return;
        var item = items[index]; Rectangle card = CardBounds(index); bool chosen = isChecked(item.Id);
        using var path = Theme.Round(new RectangleF(card.X + 1, card.Y + 1, card.Width - 3, card.Height - 3), LogicalToDeviceUnits(10));
        using var white = new SolidBrush(Color.White); g.FillPath(white, path);
        Color outline = chosen || index == current ? Theme.Accent : index == hovered ? Color.FromArgb(173, 196, 204) : Theme.Line;
        using var pen = new Pen(outline, index == current ? LogicalToDeviceUnits(2) : 1); g.DrawPath(pen, path);
        int inset = LogicalToDeviceUnits(10);
        var imageArea = new Rectangle(card.X + inset, card.Y + inset, card.Width - inset * 2, LogicalToDeviceUnits(160));
        using var background = new SolidBrush(Color.FromArgb(242, 247, 248)); g.FillRectangle(background, imageArea);
        cache.TryGetValue(item.Id, out var preview);
        if (preview?.Image is { } image)
        {
            double ratio = Math.Min((double)imageArea.Width / image.Width, (double)imageArea.Height / image.Height);
            int w = Math.Max(1, (int)(image.Width * ratio)), h = Math.Max(1, (int)(image.Height * ratio));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, imageArea.X + (imageArea.Width - w) / 2, imageArea.Y + (imageArea.Height - h) / 2, w, h);
        }
        else if (preview?.Kind.StartsWith("TEXT:", StringComparison.Ordinal) == true)
        {
            var body = Rectangle.Inflate(imageArea, -LogicalToDeviceUnits(12), -LogicalToDeviceUnits(10)); body.Y += LogicalToDeviceUnits(22); body.Height -= LogicalToDeviceUnits(22);
            TextRenderer.DrawText(g, preview.Text, bodyFont, body, Theme.Ink, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        else
        {
            string badge = preview?.Kind ?? (Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "FILE");
            var badgeRect = imageArea; badgeRect.Y += LogicalToDeviceUnits(38); badgeRect.Height = LogicalToDeviceUnits(44);
            TextRenderer.DrawText(g, badge, badgeFont, badgeRect, Theme.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            var message = Rectangle.Inflate(imageArea, -LogicalToDeviceUnits(12), 0); message.Y += LogicalToDeviceUnits(90); message.Height = LogicalToDeviceUnits(56);
            TextRenderer.DrawText(g, preview?.Text ?? "Loading preview…", smallFont, message, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
        var check = CheckBounds(card);
        bool available = item.CanRecover && selectionEnabled;
        using (var checkPath = Theme.Round(check, LogicalToDeviceUnits(4)))
        {
            using var checkFill = new SolidBrush(chosen ? Theme.Accent : available ? Color.White : Color.FromArgb(234, 239, 242));
            using var checkPen = new Pen(chosen ? Theme.Accent : Color.FromArgb(165, 184, 195), Math.Max(1, DeviceDpi / 96f));
            g.FillPath(checkFill, checkPath); g.DrawPath(checkPen, checkPath);
            if (chosen)
            {
                using var tick = new Pen(Color.White, LogicalToDeviceUnits(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(tick, [new Point(check.X + check.Width / 4, check.Y + check.Height / 2), new Point(check.X + check.Width * 9 / 20, check.Y + check.Height * 7 / 10), new Point(check.X + check.Width * 4 / 5, check.Y + check.Height * 3 / 10)]);
            }
        }
        var name = new Rectangle(card.X + LogicalToDeviceUnits(12), card.Y + LogicalToDeviceUnits(180), card.Width - LogicalToDeviceUnits(24), LogicalToDeviceUnits(40));
        TextRenderer.DrawText(g, item.Name, nameFont, name, Theme.Ink, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var info = new Rectangle(name.X, card.Y + LogicalToDeviceUnits(225), name.Width, LogicalToDeviceUnits(18));
        TextRenderer.DrawText(g, $"{Categories.Size(item.Size)}  ·  {item.Format}", smallFont, info, Theme.Muted, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        info.Y = card.Y + LogicalToDeviceUnits(248);
        Color state = item.ValidationFailed ? Color.FromArgb(170, 66, 66) : item.State is RecoveryState.AtRisk ? Color.FromArgb(166, 105, 21) : item.CanRecover ? Theme.Accent : Color.FromArgb(170, 66, 66);
        TextRenderer.DrawText(g, item.RecoveryChance, smallFont, info, state, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && index == current && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(card, -4, -4), Theme.Accent, Color.White);
    }
    private int HitTest(Point point)
    {
        int row = (point.Y + scroll.Value) / (CardHeight + Gap), column = point.X / (cardWidth + Gap);
        int index = row * columns + column;
        return column < columns && index >= 0 && index < items.Count && CardBounds(index).Contains(point) ? index : -1;
    }
    private void SetCurrent(int index)
    {
        if (index < 0 || index >= items.Count) return;
        current = index; var bounds = CardBounds(index);
        int target = scroll.Value;
        if (bounds.Top < 0) target = index / columns * (CardHeight + Gap);
        else if (bounds.Bottom > ClientSize.Height) target = index / columns * (CardHeight + Gap) + CardHeight - ClientSize.Height;
        scroll.Value = Math.Clamp(target, 0, Math.Max(0, scroll.Maximum - scroll.LargeChange + 1));
        Invalidate(); StartLoading(); CurrentItemChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e); if (e.Button != MouseButtons.Left) return;
        int index = HitTest(e.Location); if (index < 0) return;
        bool check = CheckBounds(CardBounds(index)).Contains(e.Location);
        Focus(); SetCurrent(index); if (check) ToggleCurrent();
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e); int index = HitTest(e.Location);
        if (e.Button == MouseButtons.Left && index >= 0 && !CheckBounds(CardBounds(index)).Contains(e.Location)) { SetCurrent(index); ItemActivated?.Invoke(this, EventArgs.Empty); }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); int index = HitTest(e.Location); if (index == hovered) return;
        hovered = index; Cursor = index >= 0 ? Cursors.Hand : Cursors.Default; Invalidate();
        tooltip.SetToolTip(this, index >= 0 ? items[index].Name + "\n" + items[index].OriginalPath + "\n" + items[index].Condition + "\n" + items[index].RecoveryChance + "\n" + items[index].Validation : null);
    }
    protected override void OnMouseLeave(EventArgs e) { hovered = -1; Invalidate(); base.OnMouseLeave(e); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp or Keys.Space or Keys.Enter || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        int next = e.KeyCode switch { Keys.Left => current - 1, Keys.Right => current + 1, Keys.Up => current - columns, Keys.Down => current + columns, Keys.Home => 0, Keys.End => items.Count - 1, Keys.PageDown => current + columns * Math.Max(1, ClientSize.Height / (CardHeight + Gap)), Keys.PageUp => current - columns * Math.Max(1, ClientSize.Height / (CardHeight + Gap)), _ => current };
        if (e.KeyCode == Keys.Space) spacePressed = true;
        else if (e.KeyCode == Keys.Enter) ItemActivated?.Invoke(this, EventArgs.Empty);
        else if (next != current) SetCurrent(Math.Clamp(next, 0, Math.Max(0, items.Count - 1)));
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageDown or Keys.PageUp or Keys.Space or Keys.Enter) e.Handled = e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space && spacePressed) { spacePressed = false; ToggleCurrent(); e.Handled = e.SuppressKeyPress = true; }
        base.OnKeyUp(e);
    }
    protected override void OnLostFocus(EventArgs e) { spacePressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override AccessibleObject CreateAccessibilityInstance() => new GalleryAccessibleObject(this);
    private sealed class GalleryAccessibleObject(FileGallery owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => owner.items.Count;
        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.items.Count ? new CardAccessibleObject(owner, index) : null;
    }
    private sealed class CardAccessibleObject(FileGallery owner, int index) : AccessibleObject
    {
        private readonly RecoveryItem Item = owner.items[index];
        private bool Present => index < owner.items.Count && owner.items[index].Id == Item.Id;
        public override string? Name { get => Item.Name + ", " + Item.Condition; set { } }
        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override string DefaultAction => "Select for recovery";
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override Rectangle Bounds => Present ? owner.RectangleToScreen(owner.CardBounds(index)) : Rectangle.Empty;
        public override AccessibleStates State => !Present ? AccessibleStates.Offscreen | AccessibleStates.Unavailable : AccessibleStates.Focusable | (index == owner.current ? AccessibleStates.Selected : 0) |
            (owner.Focused && index == owner.current ? AccessibleStates.Focused : 0) |
            (owner.isChecked(Item.Id) ? AccessibleStates.Checked : 0) | (!Item.CanRecover || !owner.selectionEnabled ? AccessibleStates.Unavailable : 0) |
            (!owner.ClientRectangle.IntersectsWith(owner.CardBounds(index)) ? AccessibleStates.Offscreen : 0);
        public override void DoDefaultAction() { if (Present) { owner.SetCurrent(index); owner.ToggleCurrent(); } }
    }
    protected override void Dispose(bool disposing)
    {
        disposed = true; generation++;
        if (disposing) { foreach (var value in cache.Values) value.Dispose(); cache.Clear(); tooltip.Dispose(); nameFont.Dispose(); bodyFont.Dispose(); smallFont.Dispose(); badgeFont.Dispose(); }
        base.Dispose(disposing);
    }
}

using Reclaim.App;

internal static partial class Program
{
    private static void AssertAlignment(MainForm form)
    {
        Control Named(string name) => Descendants(form).Single(x => x.Name == name);
        Rectangle Bounds(Control control)
        {
            Point point = control.Location;
            for (Control? parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
                point.Offset(parent.Location);
            return new Rectangle(point, control.Size);
        }
        void SameRow(params Control[] controls)
        {
            var bounds = controls.Select(Bounds).ToArray();
            if (bounds.Max(x => x.Top) - bounds.Min(x => x.Top) > 1 || bounds.Max(x => x.Bottom) - bounds.Min(x => x.Bottom) > 1)
                throw new Exception("Row edges do not align: " + string.Join(", ", controls.Select(x => $"{x.Name}/{x.Text}: {Bounds(x)}")));
        }
        SameRow(Named("LocationField"), Field<Control>(form, "folderBrowse"), Field<Control>(form, "imageBrowse"), Field<Control>(form, "scan"), Field<Control>(form, "cancel"));
        SameRow(Named("DriveField"), Named("ModeField"), Named("RefreshDrives"));
        SameRow(Field<Control>(form, "resultCount"), Named("SearchField"), Named("ConditionField"), Named("SelectVisible"));
        SameRow(Field<Control>(form, "openOutput"), Field<Control>(form, "recover"));
        var status = Bounds(Field<Control>(form, "status")); var recover = Bounds(Field<Control>(form, "recover"));
        if (Math.Abs(status.Top + status.Height / 2 - recover.Top - recover.Height / 2) > 1) throw new Exception("Footer status and actions are not vertically centered together.");
        var cards = Named("RecoveryStats").Controls.Cast<Control>().Select(Bounds).ToArray();
        if (cards.Max(x => x.Width) - cards.Min(x => x.Width) > 2) throw new Exception("Summary cards have unequal widths: " + string.Join(", ", cards));
        var source = Bounds(Named("SourceCard")); var workspace = Bounds(Named("ResultsWorkspace")); var details = Bounds(Named("DetailsCard"));
        if (cards.Min(x => x.Left) != source.Left || cards.Max(x => x.Right) != source.Right || workspace.Left != source.Left || workspace.Right != source.Right || details.Left != source.Left || details.Right != source.Right)
            throw new Exception("Workspace cards do not share the same left and right edges.");
        var hint = Field<Label>(form, "modeHint");
        if (hint.Height < hint.GetPreferredSize(new Size(hint.Width, 0)).Height) throw new Exception("Source instructions are clipped.");
        var hintBounds = Bounds(hint);
        if (hintBounds.Bottom > source.Bottom - Named("SourceCard").Padding.Bottom) throw new Exception("Source instructions overflow their card.");
        foreach (var frame in Descendants(form).Where(x => x.GetType().Name == "InputFrame"))
        {
            var editor = Descendants(frame).Single(x => x is TextBox or ComboBox);
            if (editor.Parent!.Height < editor.Font.Height - 1) throw new Exception($"{frame.Name} clips the native editor text.");
        }
    }
}

using AppKit;
using CoreGraphics;

namespace Yinyue.UI
{
    /// <summary>
    /// A vertically stacked form inside a tab, laid out top-down.
    ///
    /// AppKit has no StackPanel and the Windows tabs are long, so this does what the XAML
    /// does declaratively: place a row, drop the cursor, repeat. Each tab scrolls
    /// independently, so the long shortcut list does not push the other tabs around — the
    /// same rule the Windows settings window follows.
    /// </summary>
    public sealed class SettingsTab
    {
        public const double Width = 560;
        public const double LabelWidth = 170;
        public const double FieldLeft = 186;
        public const double FieldWidth = 340;

        private readonly List<NSView> _views = new();
        private double _y;

        public SettingsTab() => _y = 0;

        /// <summary>Adds a view at the cursor and drops the cursor past it.</summary>
        public T Add<T>(T view, double height, double gapAfter = 8) where T : NSView
        {
            view.Frame = new CGRect(view.Frame.X, -_y - height, view.Frame.Width, height);
            _views.Add(view);
            _y += height + gapAfter;
            return view;
        }

        public void Gap(double height) => _y += height;

        public NSTextField Heading(string text)
        {
            var label = Controls.Label(text, bold: true, size: 13);
            label.Frame = new CGRect(0, 0, Width, 18);
            return Add(label, 18, gapAfter: 6);
        }

        public NSTextField Note(string text)
        {
            var label = Controls.Label(text, size: 10, colour: Theme.Subtext);
            label.Frame = new CGRect(0, 0, Width, 14);
            label.LineBreakMode = NSLineBreakMode.ByWordWrapping;
            label.Cell.UsesSingleLineMode = false;
            return Add(label, 28, gapAfter: 4);
        }

        /// <summary>A caption on the left and a control on the right, as every row in the XAML is.</summary>
        public T Row<T>(string caption, T control, double height = 22) where T : NSView
        {
            var label = Controls.Label(caption);
            label.Frame = new CGRect(0, 0, LabelWidth, 18);

            control.Frame = new CGRect(FieldLeft, 0, control.Frame.Width > 0 ? control.Frame.Width : FieldWidth, height);

            var row = new NSView(new CGRect(0, 0, Width, Math.Max(height, 20)));
            label.Frame = new CGRect(0, (row.Frame.Height - 18) / 2, LabelWidth, 18);
            control.Frame = new CGRect(FieldLeft, 0, control.Frame.Width, height);

            row.AddSubview(label);
            row.AddSubview(control);

            Add(row, row.Frame.Height);
            return control;
        }

        /// <summary>
        /// Wraps the finished form in a scroll view. Built flipped so the first row is at the
        /// top: AppKit's origin is bottom-left, and a form that grew upward would put the
        /// heading at the bottom.
        /// </summary>
        public NSScrollView Build()
        {
            double height = _y + 12;
            var document = new DocumentView(new CGRect(0, 0, Width, height));

            foreach (var view in _views)
            {
                var f = view.Frame;
                view.Frame = new CGRect(f.X + 16, height + f.Y - 12, f.Width, f.Height);
                document.AddSubview(view);
            }

            return new NSScrollView(new CGRect(0, 0, Width + 20, 380))
            {
                DocumentView = document,
                HasVerticalScroller = true,
                DrawsBackground = false,
                BorderType = NSBorderType.NoBorder,
            };
        }

        /// <summary>
        /// A plain document view.
        ///
        /// It was called FlippedView and returned <c>IsFlipped = false</c>, which is the
        /// opposite of what the name claimed. The rows are placed by arithmetic in
        /// <see cref="Build"/> — measured down from the top and converted — so AppKit's
        /// ordinary bottom-left origin is what that arithmetic assumes, and flipping it would
        /// break the layout rather than simplify it.
        /// </summary>
        private sealed class DocumentView : NSView
        {
            public DocumentView(CGRect frame) : base(frame) { }
        }
    }

    /// <summary>Small factories, so every control in settings looks the same.</summary>
    public static class Controls
    {
        public static NSTextField Label(string text, bool bold = false, double size = 11,
                                        NSColor? colour = null) => new()
        {
            StringValue = text,
            Editable = false,
            Selectable = false,
            Bezeled = false,
            DrawsBackground = false,
            TextColor = colour ?? Theme.Text,
            Font = (bold ? NSFont.BoldSystemFontOfSize((nfloat)size) : NSFont.SystemFontOfSize((nfloat)size))
                   ?? NSFont.SystemFontOfSize(NSFont.SystemFontSize)!,
        };

        public static NSTextField Field(string value, double width = SettingsTab.FieldWidth) => new()
        {
            StringValue = value,
            Frame = new CGRect(0, 0, width, 22),
        };

        public static NSButton Check(string title, bool on)
        {
            var b = new NSButton { Title = title, Frame = new CGRect(0, 0, SettingsTab.Width, 20) };
            b.SetButtonType(NSButtonType.Switch);
            b.State = on ? NSCellStateValue.On : NSCellStateValue.Off;
            return b;
        }

        public static NSPopUpButton Choice(IEnumerable<string> items, string? selected,
                                           double width = SettingsTab.FieldWidth)
        {
            var popup = new NSPopUpButton(new CGRect(0, 0, width, 24), false);
            foreach (var item in items) popup.AddItem(item);
            if (selected is not null) popup.SelectItem(selected);
            return popup;
        }

        public static NSButton Action(string title, double width, EventHandler handler)
        {
            var b = new NSButton
            {
                Title = title,
                BezelStyle = NSBezelStyle.Rounded,
                Frame = new CGRect(0, 0, width, 24),
            };
            b.Activated += handler;
            return b;
        }
    }
}

using System.Drawing;
using System.Windows.Forms;

namespace TrayTerminal.App.Controls;

/// <summary>Dark colors for the WinForms tray menu, matching the WPF menus.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    private static readonly Color Text = Color.FromArgb(0xE6, 0xEA, 0xEE);

    public DarkMenuRenderer()
        : base(new DarkMenuColors())
    {
        RoundedEdges = false;
    }

    /// <summary>Applies the dark look and roomier rows to a menu whose items are already added.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.Padding = new Padding(0, 4, 0, 4);
        menu.MinimumSize = new Size(menu.LogicalToDeviceUnits(132), 0);
        // Drop-down menus ignore item padding, so taller rows need a fixed height.
        var height = menu.LogicalToDeviceUnits(30);
        foreach (ToolStripItem item in menu.Items)
        {
            item.AutoSize = false;
            item.Height = height;
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = Text;
        base.OnRenderItemText(e);
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        private static readonly Color Panel = Color.FromArgb(0x1C, 0x20, 0x25);
        private static readonly Color Border = Color.FromArgb(0x3D, 0x44, 0x4D);
        private static readonly Color Highlight = Color.FromArgb(0x2A, 0x30, 0x37);

        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemPressedGradientBegin => Highlight;
        public override Color MenuItemPressedGradientEnd => Highlight;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Panel;
    }
}

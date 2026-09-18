namespace CYAC.Port.Host.Menu;

/// <summary>The keys <c>menu_bar_engine</c>'s modal loop reacts to.</summary>
/// <remarks>
/// One arm per branch of the dispatch at <c>image@0x20DDD</c>, plus <see cref="Open"/>, which
/// is the ESC that got the player into the modal in the first place (<c>image@0x00E95</c>).
/// </remarks>
public enum FlightMenuKey
{
    /// <summary>ESC in flight: open the bar with the <c>?</c> menu popped.</summary>
    Open,

    /// <summary>ESC inside the modal: close with no action (<c>image@0x20F25</c>, returns 0xFFFF).</summary>
    Close,

    /// <summary>Up (0x4800) — previous item, wrapping (<c>image@0x20E31</c>).</summary>
    Up,

    /// <summary>Down (0x5000) — next item, wrapping (<c>image@0x20E73</c>).</summary>
    Down,

    /// <summary>Left (0x4B00) — previous menu, wrapping (<c>image@0x20E40</c>).</summary>
    Left,

    /// <summary>Right (0x4D00) — next menu, wrapping (<c>image@0x20E51</c>).</summary>
    Right,

    /// <summary>Home (0x4700) — the first item (<c>image@0x20E2C</c>).</summary>
    Home,

    /// <summary>End (0x4F00) — the last item (<c>image@0x20E5F</c>).</summary>
    End,

    /// <summary>PgUp (0x4900) — back to the previous section (<c>image@0x20EBC</c>).</summary>
    PageUp,

    /// <summary>PgDn (0x5100) — forward to the next section (<c>image@0x20E8C</c>).</summary>
    PageDown,

    /// <summary>Enter (0x0D) — activate the highlighted item (<c>image@0x20F2A</c>).</summary>
    Enter,

    /// <summary>
    /// M2 PORT ADDITION — open the Port Settings dialog directly, without walking the bar to the
    /// <c>?</c> menu's row.  There is no such key in the original; it exists so a headless
    /// <c>--script</c> can reach the dialog in one word (<c>settings</c>).
    /// </summary>
    Settings,

    /// <summary>
    /// M3 PORT ADDITION — open the read-only Mission Stats dialog directly, without walking the
    /// bar to the <c>?</c> menu's row.  There is no such key in the original; it exists so a
    /// headless <c>--script</c> can reach the panel in one word (<c>stats</c>).
    /// </summary>
    Stats,

    /// <summary>
    /// M2 PORT ADDITION — reset the highlighted settings row to its built-in default.  At the
    /// window this is the <c>R</c> key, which arrives as type-ahead; the script word is
    /// <c>settingsreset</c>.
    /// </summary>
    Reset,
}

/// <summary>
/// The in-flight menu's MODAL SELECTION: which menu is popped, which row is highlighted, and how the
/// engine's keys move that.
/// </summary>
/// <remarks>
/// <para>
/// Pure state and pure decisions — no pixels, no session, no options.
/// </para>
/// <para>
/// <b>Sections</b> are the <c>*</c> tails of the item records: an item whose record ends in
/// <c>*</c> is the LAST of its section, and a horizontal rule is drawn under it.  PgDn therefore
/// means "the first item after the next section end at or below me", PgUp "the first item of the
/// previous section".
/// </para>
/// </remarks>
public sealed class FlightMenuState
{
    private readonly FlightMenuTable _table;

    /// <summary>Creates the selection over a menu table.</summary>
    /// <param name="table">The six menus.</param>
    public FlightMenuState(FlightMenuTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
    }

    /// <summary>Whether the bar is up and the simulation is therefore frozen.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Which menu is popped, 0..5.</summary>
    public int MenuIndex { get; private set; }

    /// <summary>Which row of it is highlighted.</summary>
    public int ItemIndex { get; private set; }

    /// <summary>The six menus.</summary>
    public IReadOnlyList<FlightMenu> Menus => _table.Menus;

    /// <summary>The popped menu.</summary>
    public FlightMenu Menu => _table.Menus[MenuIndex];

    /// <summary>The highlighted row.</summary>
    public FlightMenuItem Item => Menu.Items[ItemIndex];

    /// <summary>
    /// Opens the bar with menu 0 item 0 selected — <c>menu_bar_engine</c>'s own initial state
    /// (<c>image@0x20DAD</c>: <c>sub di, di; mov [bp-4], di</c>).
    /// </summary>
    public void Open()
    {
        IsOpen = true;
        MenuIndex = 0;
        ItemIndex = 0;
    }

    /// <summary>Closes the bar.</summary>
    public void Close() => IsOpen = false;

    /// <summary>
    /// Applies one of the engine's keys.  Returns the item to ACTIVATE when the key was Enter on a
    /// row, and null otherwise.
    /// </summary>
    /// <param name="key">The key.</param>
    public FlightMenuItem? Press(FlightMenuKey key)
    {
        if (!IsOpen)
        {
            if (key == FlightMenuKey.Open)
            {
                Open();
            }

            return null;
        }

        int count = Menu.Items.Count;
        switch (key)
        {
            case FlightMenuKey.Open:
            case FlightMenuKey.Close:
                Close();
                return null;
            case FlightMenuKey.Left:
                MenuIndex = (MenuIndex + _table.Menus.Count - 1) % _table.Menus.Count;
                ItemIndex = 0;
                return null;
            case FlightMenuKey.Right:
                MenuIndex = (MenuIndex + 1) % _table.Menus.Count;
                ItemIndex = 0;
                return null;
            case FlightMenuKey.Up:
                ItemIndex = (ItemIndex + count - 1) % count;
                return null;
            case FlightMenuKey.Down:
                ItemIndex = (ItemIndex + 1) % count;
                return null;
            case FlightMenuKey.Home:
                ItemIndex = 0;
                return null;
            case FlightMenuKey.End:
                ItemIndex = count - 1;
                return null;
            case FlightMenuKey.PageDown:
                ItemIndex = NextSection();
                return null;
            case FlightMenuKey.PageUp:
                ItemIndex = PreviousSection();
                return null;
            case FlightMenuKey.Enter:
                return Item;
            default:
                return null;
        }
    }

    /// <summary>
    /// The engine's TYPE-AHEAD: move to the next row whose label starts with this character,
    /// searching forward from the row after the highlighted one and wrapping.
    /// </summary>
    /// <param name="c">The character typed.</param>
    /// <returns>True when a row matched.</returns>
    /// <remarks>
    /// <c>menu_bar_engine</c>'s "else" arm (<c>image@0x20E1F</c> → <c>image@0x20EDA</c>) searches
    /// the open menu's own items; the port matches case-insensitively so a capitalised label
    /// answers a lower-case key.
    /// </remarks>
    public bool TypeAhead(char c)
    {
        if (!IsOpen)
        {
            return false;
        }

        IReadOnlyList<FlightMenuItem> items = Menu.Items;
        for (int step = 1; step <= items.Count; step++)
        {
            int candidate = (ItemIndex + step) % items.Count;
            string label = items[candidate].Label;
            if (label.Length > 0 && char.ToUpperInvariant(label[0]) == char.ToUpperInvariant(c))
            {
                ItemIndex = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Where PgDn lands: the first row of the section after the highlighted row's.</summary>
    private int NextSection()
    {
        IReadOnlyList<FlightMenuItem> items = Menu.Items;
        for (int i = ItemIndex; i < items.Count; i++)
        {
            if (items[i].SectionEnd)
            {
                return i + 1 < items.Count ? i + 1 : items.Count - 1;
            }
        }

        return items.Count - 1;
    }

    /// <summary>Where PgUp lands: the first row of the section before the highlighted row's.</summary>
    private int PreviousSection()
    {
        IReadOnlyList<FlightMenuItem> items = Menu.Items;

        // The first row of the section the highlight is in.
        int start = 0;
        for (int i = ItemIndex - 1; i >= 0; i--)
        {
            if (items[i].SectionEnd)
            {
                start = i + 1;
                break;
            }
        }

        if (start == 0)
        {
            return 0;
        }

        // …and the first row of the one before it.
        for (int i = start - 2; i >= 0; i--)
        {
            if (items[i].SectionEnd)
            {
                return i + 1;
            }
        }

        return 0;
    }
}

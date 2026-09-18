namespace mode13hx.Model;

// Representation of a control (action which can be bound to a key or any other input)
public class Control
{
    public readonly ControlEnum Id;
    public bool Active;
    public int Delta;

    private Control(ControlEnum id)
    {
        Id = id;
        Active = false;
        Interlocked.Exchange(ref Delta, 0);
    }
    
    private static readonly Dictionary<ControlEnum, Control> Controls = new();

    // The registry is static and shared, so its read-then-insert has to be atomic: building two
    // options objects on two threads at once otherwise raced and one of them threw
    // "An item with the same key has already been added". Single-threaded callers are unaffected —
    // an uncontended lock is a few nanoseconds and Create runs a handful of times per process.
    private static readonly object ControlsGate = new();

    // factory method for controls
    public static Control Create(ControlEnum id)
    {
        lock (ControlsGate)
        {
            if (Controls.TryGetValue(id, out Control control)) return control;
            control = new Control(id);
            Controls.Add(id, control);
            return control;
        }
    }

    // Same registry, same gate: enumerating a Dictionary while another thread inserts into it throws.
    public static void Reset()
    {
        lock (ControlsGate) { foreach (Control control in Controls.Values) { control.Active = false; } }
    }
}

// ReSharper disable InconsistentNaming
public enum ControlEnum
{
    VK_FORWARD = 100,
    VK_BACKWARD,
    VK_TURN_LEFT,
    VK_TURN_RIGHT,
    VK_USE,
    MOUSE_DELTA_X = 500,
    MOUSE_DELTA_Y,
    MOUSE_DELTA_WHEEL,
    MOUSE_BUTTON_LEFT,
    MOUSE_BUTTON_RIGHT,

    // Base for application-defined controls. An application that needs more actions than the
    // built-in set declares its own as APP_BASE + n (they are ordinary ControlEnum values, so
    // Control.Create / CommonOptions.KbControls / Control.Reset all work unchanged) without the
    // library having to know their names.
    APP_BASE = 1000,
}
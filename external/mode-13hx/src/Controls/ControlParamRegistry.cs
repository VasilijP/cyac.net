namespace mode13hx.Controls;

// Registry of control parameters.
// This could be used for parameter listing or programmatic manipulation (e.g. console in a UI).
public static class ControlParamRegistry
{
    private static readonly Dictionary<string, object> Params = new();

    // Registers new or retrieves an existing parameter.
    public static ControlParam<T> Get<T>(string name, T initialValue = default, Func<T, ControlParam<T>, bool> validator = null)
    {
        if (Params.TryGetValue(name, out object existingParam)) { return (ControlParam<T>)existingParam; }
        ControlParam<T> param = new(initialValue) { Name = name, Validator = validator };
        Params[name] = param;
        return param;
    }
    
    // TODO: add some listing capabilities(?)
}

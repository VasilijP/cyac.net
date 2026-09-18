namespace mode13hx.Controls;

public sealed class ControlParam<T>(T initialValue = default)
{
    public T DefaultValue { get; } = initialValue;
    private T value = initialValue;
    
    public string Name { get; set; }
    public string Description { get; set; }
    
    public event Action<T, ControlParam<T>> ValueChanged; // optional trigger when the value changes
    public Func<T, ControlParam<T>, bool> Validator { get; set; } // optional validation function
    
    // TODO: consider using mutex for thread safe parameters (this could cause unnecessary performance issues for ST rendering)
    public T Value
    {
        get => value;
        set
        {
            if (Validator != null && !Validator(value, this)) { return; } // ignore - if exception would be desired then validator could throw it
            if (Equals(this.value, value)) return; // skip if there is no change
            this.value = value;
            ValueChanged?.Invoke(value, this); // invoke the optional action
        }
    }

    // Resets the value to the default
    public void Reset() => Value = DefaultValue;
}

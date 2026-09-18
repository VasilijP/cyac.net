namespace mode13hx.Presentation;

public class FrametimeBucket
{
    private double frametimeSum;
    private double minFrametime;
    private double maxFrametime;
    //private readonly List<double> frametimes = [];
    public int Count;// => frametimes.Count;

    public double AverageFrametime => Count > 0 ? frametimeSum / Count : 0;
    public double MinFrametime => Count > 0 ? minFrametime : 0;
    public double MaxFrametime => Count > 0 ? maxFrametime : 0;

    public FrametimeBucket() { Clear(); }
    
    public void Clear()
    {
        frametimeSum = 0;
        minFrametime = double.MaxValue;
        maxFrametime = double.MinValue;
        Count = 0;//frametimes.Clear();
    }

    // Accumulate a new frametime value
    public void AddFrametime(double frametimeMilliseconds)
    {
        frametimeSum += frametimeMilliseconds;
        minFrametime = Math.Min(minFrametime, frametimeMilliseconds);
        maxFrametime = Math.Max(maxFrametime, frametimeMilliseconds);
        ++Count; //frametimes.Add(frametimeMilliseconds);
    }
}

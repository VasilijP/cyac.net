using System.Diagnostics;

namespace mode13hx.Presentation;

public class FrametimeComponent
{
    private readonly FrametimeBucket[] buckets;
    public int BucketCount => buckets.Length;
    
    private int currentBucketIndex = 0;
    private readonly double bucketDurationSeconds;
    private double lastBucketShiftTime = 0;
    private readonly Stopwatch stopwatch;

    public FrametimeComponent(int numBuckets, double historyDepthSeconds)
    {
        this.buckets = new FrametimeBucket[numBuckets+1];
        for (int i = 0; i < buckets.Length; i++) { buckets[i] = new FrametimeBucket(); }
        this.bucketDurationSeconds = historyDepthSeconds / numBuckets;
        this.stopwatch = Stopwatch.StartNew();
    }

    public void RecordFrame(double frametimeSeconds)
    {
        double currentTimeSeconds = stopwatch.Elapsed.TotalSeconds;
        if (currentTimeSeconds - lastBucketShiftTime > bucketDurationSeconds) // move to the next bucket and clear the old data
        {
            currentBucketIndex = (currentBucketIndex + 1) % buckets.Length;
            buckets[currentBucketIndex].Clear();
            lastBucketShiftTime = currentTimeSeconds;
        }
        buckets[currentBucketIndex].AddFrametime(1000.0*frametimeSeconds); // add to current bucket
    }

    public double GetAverageFrametime()
    {
        double totalFrametime = buckets.Sum(bucket => bucket.AverageFrametime * bucket.Count);
        int totalCount = buckets.Sum(bucket => bucket.Count);
        return totalCount > 0 ? totalFrametime / totalCount : 0;
    }

    // min, average, max
    public Tuple<double, double, double> GetFrametimeAtBucket(int i) 
    {   
        int index = (i+currentBucketIndex) % buckets.Length;
        return new Tuple<double, double, double>(buckets[index].MinFrametime, buckets[index].AverageFrametime, buckets[index].MaxFrametime); 
    }
    public double GetMinFrametime() { return (from bucket in buckets where bucket.Count > 0 select bucket.MinFrametime).Prepend(1000.0).Min(); }
    public double GetMaxFrametime() { return (from bucket in buckets where bucket.Count > 0 select bucket.MaxFrametime).Prepend(0.0).Max(); }
    public static double FrametimeToFps(double frametimeMilliseconds) { return frametimeMilliseconds > 0 ? 1000.0 / frametimeMilliseconds : 0; }
}

using mode13hx.Model;

namespace mode13hx.Util;

public static class Func
{
     
    public static void Swap(ref int a, ref int b) { (a, b) = (b, a); }

    // vector length
    public static double Vectlen(double x, double y) { return Math.Sqrt(x * x + y * y); }

    // distance between two points
    public static double SegLength(double x1, double y1, double x2, double y2) { return Vectlen(x1 - x2, y1 - y2); }

    // vector normalization to 1.0 length
    public static void Unitvector(double x1, double y1, out double x2, out double y2)
    {
        double norm = Vectlen(x1, y1);
        x2 = x1 / norm;
        y2 = y1 / norm;
    }

    // distance of point P from the line A1A2
    public static double Dist(double px, double py, double a1X, double a1Y, double a2X, double a2Y)
    {
        double sax = a2X - a1X; // double nay = sax;
        double say = a2Y - a1Y; // double nax = -say;
        Intersection3(a1X,a1Y,a2X,a2Y,px,py,px-say,py+sax,out double p1X, out double p1Y);
        double mind = Math.Min(SegLength(px,py,a1X,a1Y),SegLength(px,py,a2X,a2Y));
        return Math.Min(mind,SegLength(p1X,p1Y,px,py));
    }
    
    // intersection of two lines, if they are parallel, then return infinity
    private static void Intersection3(double a1X, double a1Y, double a2X, double a2Y, double b1X, double b1Y, double b2X, double b2Y, out double px, out double py)
    {
        double det = (a2X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(a2Y-a1Y);
        if (Math.Abs(det) > Constant.Eps)
        {
            double dt = (b1X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(b1Y-a1Y);
            double dp = (a2X-a1X)*(b1Y-a1Y)-(b1X-a1X)*(a2Y-a1Y);
            double t = dt/det;
            double p = dp/det;
         
            if ((t>=0)&&(t<=1)&&(p>=-1)&&(p<=1)) // we don't know which normal got to us
            {
                px = a1X+(a2X-a1X)*t;
                py = a1Y+(a2Y-a1Y)*t;
            } 
            else { px = py = Constant.Infinity; }
        } 
        else { px = py = Constant.Infinity; }
    }

    // halfline a1 to a2 extending beyond a2 intersected by segment b1, b2
    public static void Intersection2(double a1X, double a1Y, double a2X, double a2Y, double b1X, double b1Y, double b2X, double b2Y, out double px, out double py, out double tc, out bool ex)
    {
        double d = (a2X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(a2Y-a1Y);
        px = a1X; py = a1Y; tc = 0.0; // won't be used
        ex = Math.Abs(d) > Constant.Eps; // if 
        if (ex)
        {
            double dt = (b1X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(b1Y-a1Y);
            double dp = (a2X-a1X)*(b1Y-a1Y)-(b1X-a1X)*(a2Y-a1Y);
            double t = dt/d;
            double p = dp/d;
            ex = (t >= 0)&&(p>=0)&&(p<=1);
            px = a1X+(a2X-a1X)*t;
            py = a1Y+(a2Y-a1Y)*t;
            tc = p;
        }
    }

    // body us1>a1,a2, us2>b1,b2, intersection exists?
    public static void Intersection1(double a1X, double a1Y, double a2X, double a2Y, double b1X, double b1Y, double b2X, double b2Y, out double px, out double py, out bool ex)
    {
        double det = (a2X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(a2Y-a1Y);
        px = a1X; py = a1Y; // won't be used
        ex = Math.Abs(det) > Constant.Eps; // if 
        if (ex) 
        {
            double dt = (b1X-a1X)*(b1Y-b2Y)-(b1X-b2X)*(b1Y-a1Y);
            double dp = (a2X-a1X)*(b1Y-a1Y)-(b1X-a1X)*(a2Y-a1Y);
            double t = dt/det;
            double p = dp/det;
            ex = (t>=0)&&(t<=1)&&(p>=0)&&(p<=1);
            px = a1X+(a2X-a1X)*t;
            py = a1Y+(a2Y-a1Y)*t;
        }
    }

    // RGB format TODO: make sure this is multiplatform compatible
    public static uint EncodePixelColor(int r, int g, int b)
    {
        if (BitConverter.IsLittleEndian) { return (uint)(((b & 0xFF) << 16) | ((g & 0xFF) << 8) | (r & 0xFF)); }
                                           return (uint)(((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF));
    }

    // ARGB format TODO: make sure this is multiplatform compatible
    public static uint EncodePixelColorRgba(int r, int g, int b, int a)
    {
        if (BitConverter.IsLittleEndian) { return (uint)(((a & 0xFF) << 24) | ((b & 0xFF) << 16) | ((g & 0xFF) << 8) | (r & 0xFF)); }
                                           return (uint)(((r & 0xFF) << 24) | ((g & 0xFF) << 16) | ((b & 0xFF) << 8) | (a & 0xFF));
    }

    public static void DecodePixelColor(uint color, out int r, out int g, out int b)
    {
        if (BitConverter.IsLittleEndian) { r = (int)(color & 0xFF);         g = (int)((color >> 8) & 0xFF); b = (int)((color >> 16) & 0xFF); }
        else                             { r = (int)((color >> 16) & 0xFF); g = (int)((color >> 8) & 0xFF); b = (int)(color & 0xFF); }
    }

    // makes color darker (or lighter) by a given factor
    public static uint Darker(uint barColor, double ratio = 0.75) 
    {
        DecodePixelColor(barColor, out int r, out int g, out int b);
        r = Math.Clamp((int)(r*ratio), 0, 255); g = Math.Clamp((int)(g*ratio), 0, 255); b = Math.Clamp((int)(b*ratio), 0, 255);
        return EncodePixelColor(r, g, b);
    }
    
    public static uint MixColorsAdd(uint color1, float w1, uint color2, float w2)
    {
        DecodePixelColor(color1, out int r1, out int g1, out int b1);
        DecodePixelColor(color2, out int r2, out int g2, out int b2);
        float w = w2 / (w1 + w2);
        r1 += (int)(w*r2); g1 += (int)(w*g2); b1 += (int)(w*b2);
        int max = Math.Max(Math.Max(r1, g1), b1);
        float wc = Math.Min(255.0f, max) / max;
        int r = (int)Math.Clamp(r1*wc, 0, 255);
        int g = (int)Math.Clamp(g1*wc, 0, 255);
        int b = (int)Math.Clamp(b1*wc, 0, 255);
    
        return EncodePixelColor(r, g, b);
    }

    public static uint MixColors(uint color1, float weight1, uint color2, float weight2)
    {
        DecodePixelColor(color1, out int r1, out int g1, out int b1);
        DecodePixelColor(color2, out int r2, out int g2, out int b2);
        r1 = 255 - r1; g1 = 255 - g1; b1 = 255 - b1;
        r2 = 255 - r2; g2 = 255 - g2; b2 = 255 - b2;
        float sumW = weight1 + weight2;
        weight1 /= sumW;  weight2 /= sumW; // normalize weights to have sum = 1.0
        int r = Math.Clamp((int)(r1*weight1 + r2*weight2), 0, 255);
        int g = Math.Clamp((int)(g1*weight1 + g2*weight2), 0, 255);
        int b = Math.Clamp((int)(b1*weight1 + b2*weight2), 0, 255);
        return EncodePixelColor(255-r, 255-g, 255-b);
    }

    public static uint MixColorsInverted(uint color1, float weight1, uint color2, float weight2)
    {
        DecodePixelColor(color1, out int r1, out int g1, out int b1);
        DecodePixelColor(color2, out int r2, out int g2, out int b2);
        r1 = 255 - r1; g1 = 255 - g1; b1 = 255 - b1;
        r2 = 255 - r2; g2 = 255 - g2; b2 = 255 - b2;
        float sumW = weight1 + weight2;
        weight2 /= sumW; // normalize weights to have sum = 1.0
        int r = Math.Clamp((int)(r1 + Math.Abs(r1-r2)*weight2), 0, Math.Max(r1, r2));
        int g = Math.Clamp((int)(g1 + Math.Abs(g1-g2)*weight2), 0, Math.Max(g1, g2));
        int b = Math.Clamp((int)(b1 + Math.Abs(b1-b2)*weight2), 0, Math.Max(b1, b2));
        return EncodePixelColor(255-r, 255-g, 255-b);
    }
}

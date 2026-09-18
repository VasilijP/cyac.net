using mode13hx.Util;
using tgalib_core;

namespace mode13hx.Model;

public class Texture : IImage
{
    // file name
    public string Name { get; set; }
    
    // is loaded flag
    public bool Loaded { get; set; }
    
    // data [x, y] @ 32 bit RGBA
    public uint[] Data;
    public readonly List<uint[]> Variant = [];
    public readonly Dictionary<string, uint[]> MipMap = new(); // width_height_variant -> data
    public int Width;
    int IImage.Width => Width;
    public int Height;
    int IImage.Height => Height;
    
    public static Texture CreateBlank(int width, int height, uint color)
    {
        Texture texture = new()
        {
            Name = "blank_"+Guid.NewGuid()+".tga",
            Loaded = true, 
            Data = new uint[width * height],
            Width = width,
            Height = height
        };
        
        Span<uint> s = new(texture.Data, 0, width * height);
        s.Fill(color);
        
        return texture;
    }
    
    public void LoadData()
    {
        if (Data == null)
        {
            TgaImage bitmap = new(Name);
            Width = bitmap.Width;
            Height = bitmap.Height;

            // Convert the bitmap to a uint array
            Data = new uint[bitmap.Width * bitmap.Height];
            int index = 0;

            for (int x = 0; x < bitmap.Width; x++) {
                for (int y = 0; y < bitmap.Height; y++) // Store by y to form contiguous data (columns)
                {
                    bitmap.GetPixelRgba(x, Height - y - 1, out int r, out int g, out int b, out int a); // Flipped y-axis from TGA to texture coordinates
                    Data[index++] = Func.EncodePixelColorRgba(r, g, b, a);
                }
            }
        }

        // Load all variants of this texture
        foreach (int variantNumber in GetVariants())
        {
            if (variantNumber < Variant.Count) continue; // Only load if not already loaded
            string variantFileName = $"{Path.GetFileNameWithoutExtension(Name)}_{variantNumber:0000}{Path.GetExtension(Name)}";
            TgaImage bitmapV = new(variantFileName);

            if (bitmapV.Width != Width || bitmapV.Height != Height) { throw new Exception($"Variant {variantNumber} has different size than original texture!"); }

            uint[] variantData = new uint[bitmapV.Width * bitmapV.Height];
            int variantIndex = 0;

            for (int x = 0; x < bitmapV.Width; x++) {
                for (int y = 0; y < bitmapV.Height; y++) // Store by y to form contiguous data for columns
                {
                    bitmapV.GetPixelRgba(x, Height - y - 1, out int r, out int g, out int b, out int a); // Flipped y-axis from TGA to texture coordinates
                    variantData[variantIndex++] = Func.EncodePixelColorRgba(r, g, b, a);
                }
            }

            Variant.Add(variantData);
        }
        
        Loaded = true;
    }
    
    public void PrepareMipMap(int width, int height, int variant = -1)
    {
        if (!Loaded) { LoadData(); }
        
        if (Width % width != 0 || Height % height != 0) { throw new Exception($"MipMap width and height must be a divisor of texture width and height!"); }
        if (Width / width != Height / height) { throw new Exception($"MipMap width and height must be a (single) divisor of texture width and height!"); }
        int d = Width / width;
        
        string key = $"{width}_{height}_{variant}";
        if (MipMap.ContainsKey(key)) { return; }
    
        uint[] data = Data;
        if (variant >= 0) { data = Variant[variant]; }
        
        // resample data into mipmap
        uint[] mipmap = new uint[width * height];
        for (int x = 0; x < width; x++)
        {
            for (int y =0; y < height; y++)
            {
                int r = 0; int g = 0; int b = 0;
                for (int dx = 0; dx < d; dx++)
                {
                    for (int dy = 0; dy < d; dy++)
                    {
                        uint pixel = data[(x*d + dx) * Height + y*d + dy];
                        Func.DecodePixelColor(pixel, out int rf, out int gf, out int bf);
                        r += rf; g += gf; b += bf;
                    }
                }
                uint filtered = Func.EncodePixelColor(r / (d * d), g / (d * d), b / (d * d));
                mipmap[x * height + y] = filtered;
            }
        }
        MipMap[key] = mipmap;
    }
    
    // Scale the texture to the specified dimensions
    public void Scale(int newWidth, int newHeight)
    {
        if (!Loaded) { LoadData(); } // Ensure the texture data is loaded
        if (Variant.Count > 0) { throw new Exception("Cannot scale texture with variants!"); }

        // Compute scaling ratios using fixed-point arithmetic (16.16 format)
        int xRatio = (Width << 16) / newWidth + 1;
        int yRatio = (Height << 16) / newHeight + 1;
        
        int[] sourceXs = new int[newWidth], sourceYs = new int[newHeight];
        for (int i = 0; i < newWidth; i++) { sourceXs[i] = Math.Clamp((i * xRatio) >> 16, 0, Width - 1); } // Precompute source X indices
        for (int j = 0; j < newHeight; j++) { sourceYs[j] = Math.Clamp((j * yRatio) >> 16, 0, Height - 1); } // Precompute source Y indices

        uint[] newData = new uint[newWidth * newHeight];
        for (int ix = 0; ix < newWidth; ++ix) // Loop over columns (x-axis)
        {
            int frameColumnIndex = ix * newHeight;
            int textureColumnIndex = sourceXs[ix] * Height;
            for (int j = 0; j < newHeight; ++j) // Loop over rows (y-axis) within the column
            {
                newData[frameColumnIndex + j] = Data[textureColumnIndex + sourceYs[j]];
            }
        }
        
        Data = newData;
        Width = newWidth;
        Height = newHeight;
    }
    
    // this will list all available variants of this texture by suffix number, e.g. texture.tga -> texture_0001.tga, texture_0002.tga, etc.
    public IEnumerable<int> GetVariants()
    {
        string fileName = Path.GetFileNameWithoutExtension(Name);
        string extension = Path.GetExtension(Name);
        int variantNumber = 0;
        while (true)
        {
            string variantSuffixFormatted = variantNumber.ToString("0000");
            string variantFileName = $"{fileName}_{variantSuffixFormatted}{extension}";
            if (!File.Exists(variantFileName)) { yield break; }
            yield return variantNumber;
            variantNumber++;
        }
    }
    
    public void DecodeToVariants(int frameWidth, int frameHeight)
    {
        if (!Loaded) { LoadData(); } // Ensure the texture data is loaded
        int totalFrames = Width; // Each frame forms a column in the large texture -> width is a count of frames
        if (Height != frameWidth * frameHeight) // Each frame is unrolled into a column -> height is a frameWidth * frameHeight
        {
            throw new InvalidOperationException("Texture dimensions do not match expected dimensions for decoding.");
        }

        for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
        {
            // Create a new variant for this frame
            uint[] frameData = new uint[frameWidth * frameHeight];

            // Extract the frame data from the large texture
            for (int x = 0; x < frameWidth; x++)
            {
                int sourceIndex = frameIndex * Height + x * frameHeight;
                for (int y = 0; y < frameHeight; y++, sourceIndex++)
                {
                    frameData[x * frameHeight + y] = Data[sourceIndex];
                }
            }

            // Add the frame data as a new variant
            Variant.Add(frameData);
        }
        
        this.Width = frameWidth;
        this.Height = frameHeight;
        this.Data = new uint[Width * Height];
        Array.Copy(Variant[0], this.Data, Variant[0].Length);
    }
    
    // save data to file, returns true if successful
    public bool SaveVariant(int variantNumber = -1, bool overwrite = false)
    {
        if (variantNumber == -1) { variantNumber = Variant.Count; } // add new variant

        string name = Path.GetFileNameWithoutExtension(Name);
        string variantSuffixFormatted = variantNumber.ToString("0000");
        string fileName = $"{name}_{variantSuffixFormatted}.tga";
        if (File.Exists(fileName) && !overwrite) { return false; }
        
        using FileStream stream = new FileStream(fileName, FileMode.Create);
        TgaFileFormat.CommonSave(TgaMode.Rgb24Rle, stream, this);

        // Add copy of current texture to variant list
        uint[] variantData = new uint[Data.Length];
        Array.Copy(Data, variantData, Data.Length);
        Variant.Add(variantData);
        return true;
    }

    // get pixel color by coordinates, this is slow and used only for single pixel access (e.g. color picker)
    public uint GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) { return 0; } 
        return Data[x * Height + y];
    }
    
    // For the purpose of saving in TGA format, Y coordinate is flipped
    void IImage.GetPixelRgba(int x, int y, out int r, out int g, out int b, out int a)
    {
        a = 0xFF; // Assume full opacity
        uint color = Data[x * Height + (Height - y - 1)];
        Func.DecodePixelColor(color, out r, out g, out b);
    }
}
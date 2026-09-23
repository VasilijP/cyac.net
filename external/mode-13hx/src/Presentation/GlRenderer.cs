using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace mode13hx.Presentation;

/// <summary>
/// The OpenGL presenter: one texture the CPU-rasterised frame is uploaded into every frame, drawn on
/// one full-screen quad.  OpenGL 3.3 core, forward-compatible, so it runs on every desktop driver
/// including Apple's frozen 4.1 - the point of this presenter beside the Vulkan one (VENDOR.md).
/// Selected with --gfx opengl (the default); --gfx vulkan keeps the Vulkan presenter and its
/// compute-shader frame decompression.
/// </summary>
/// <remarks>
/// The frame is stored column-major (a column of pixels is contiguous, see Canvas), so the texture is
/// frame-height wide and frame-width tall and the quad's texture coordinates turn it back; the
/// vertices and the shaders (resources/gl.vert, gl.frag) are upstream main's, which present that
/// layout the right way up.
/// </remarks>
public sealed unsafe class GlRenderer : IDisposable
{
    private readonly GL gl;
    private readonly IWindow window;
    private readonly uint textureWidth;   // = frame height
    private readonly uint textureHeight;  // = frame width
    private uint texture, program, vao, vbo, ebo;

    // Full-screen quad: Position (vec3) + TexCoord (vec2).  In NDC (0, 0) is the centre of the screen.
    private static readonly float[] Vertices =
    [
        // Position          TexCoord
         1.0f,  1.0f, 0.0f,  0.0f, 1.0f, // top right
         1.0f, -1.0f, 0.0f,  1.0f, 1.0f, // bottom right
        -1.0f, -1.0f, 0.0f,  1.0f, 0.0f, // bottom left
        -1.0f,  1.0f, 0.0f,  0.0f, 0.0f, // top left
    ];

    private static readonly uint[] Indices = [0, 1, 3, 1, 2, 3];

    public GlRenderer(IWindow window, int frameWidth, int frameHeight)
    {
        this.window = window;
        gl = GL.GetApi(window);
        textureWidth = (uint)frameHeight;
        textureHeight = (uint)frameWidth;

        Console.WriteLine($"[OpenGL] {gl.GetStringS(StringName.Renderer)} - {gl.GetStringS(StringName.Version)}");

        program = BuildProgram(
            File.ReadAllText(ResolvePath("resources/gl.vert")),
            File.ReadAllText(ResolvePath("resources/gl.frag")));

        vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);

        vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* v = Vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Vertices.Length * sizeof(float)), v, BufferUsageARB.StaticDraw);

        ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        fixed (uint* i = Indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(Indices.Length * sizeof(uint)), i, BufferUsageARB.StaticDraw);

        // Locations 0 and 1 are fixed in shader.vert (layout(location = n)).
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));

        texture = gl.GenTexture();
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, textureWidth, textureHeight, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);  // scale down: filter
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest); // scale up: pixels
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        gl.UseProgram(program);
        gl.Uniform1(gl.GetUniformLocation(program, "texture0"), 0);
        gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);

        Resize(window.FramebufferSize);
        window.FramebufferResize += Resize;
    }

    /// <summary>Upload one frame (frame-width x frame-height pixels, column-major) and draw it.</summary>
    /// <remarks>
    /// glTexSubImage2D copies the client memory before it returns (no pixel-unpack buffer is bound), so
    /// the caller may release the frame slot as soon as this returns.
    /// </remarks>
    public void Present(uint* pixels)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, textureWidth, textureHeight,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.UseProgram(program);
        gl.BindVertexArray(vao);
        gl.DrawElements(PrimitiveType.Triangles, (uint)Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
    }

    private void Resize(Vector2D<int> size) => gl.Viewport(0, 0, (uint)Math.Max(1, size.X), (uint)Math.Max(1, size.Y));

    private uint BuildProgram(string vertexSource, string fragmentSource)
    {
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);
        uint prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        gl.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new Exception("OpenGL program link failed: " + gl.GetProgramInfoLog(prog));
        gl.DetachShader(prog, vs);
        gl.DetachShader(prog, fs);
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new Exception($"OpenGL {type} compile failed: " + gl.GetShaderInfoLog(shader));
        return shader;
    }

    private static string ResolvePath(string relativePath) =>
        Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(AppContext.BaseDirectory, relativePath);

    public void Dispose()
    {
        window.FramebufferResize -= Resize;
        gl.DeleteTexture(texture);
        gl.DeleteBuffer(vbo);
        gl.DeleteBuffer(ebo);
        gl.DeleteVertexArray(vao);
        gl.DeleteProgram(program);
        gl.Dispose();
    }
}

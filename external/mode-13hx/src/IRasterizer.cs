using mode13hx.Presentation;

namespace mode13hx;

// Implementation of this is required to produce current image of the world/scene.  
public interface IRasterizer
{
    public void Render(FrameBuffer buffer, double secondsSinceLastFrame);
}

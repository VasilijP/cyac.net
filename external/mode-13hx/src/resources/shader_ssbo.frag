#version 450

layout(location = 0) in vec2 fragTexCoord;

layout(set = 0, binding = 0) readonly buffer FramePixels { uint pixels[]; };
layout(push_constant) uniform PushConstants { uint Width; uint Height; uint FrameOffset; };

layout(location = 0) out vec4 outColor;

void main()
{
    uint x = min(uint(fragTexCoord.x * float(Width)), Width - 1u);
    uint y = min(uint(fragTexCoord.y * float(Height)), Height - 1u);
    outColor = unpackUnorm4x8(pixels[FrameOffset + y * Width + x]);
}

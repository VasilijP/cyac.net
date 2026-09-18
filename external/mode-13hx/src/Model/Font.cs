namespace mode13hx.Model;

// Oldschool bitmap font ( https://github.com/susam/pcface )
public sealed class Font
{
    public readonly Texture Texture;
    public readonly int CharacterWidth;
    public readonly int CharacterHeight;
    
    public Font(string name, int characterWidth, int characterHeight)
    {
        Texture = new Texture() { Name = name };
        Texture.LoadData();
        CharacterWidth = characterWidth;
        CharacterHeight = characterHeight;
    }
}

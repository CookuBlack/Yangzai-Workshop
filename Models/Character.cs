namespace YangzaiWorkshop.Models;

public class CharacterInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string AvatarPath { get; set; } = string.Empty;
    public List<string> ImagePaths { get; set; } = new();
}

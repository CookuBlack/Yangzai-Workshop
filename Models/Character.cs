namespace YangzaiWorkshop.Models;

public class CharacterInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Personality { get; set; } = string.Empty;
    public string AvatarPath { get; set; } = string.Empty;
    public List<string> ImagePaths { get; set; } = new();
    public List<string> AudioPaths { get; set; } = new();
    public List<CharacterRelationship> Relationships { get; set; } = new();
}

/// <summary>人物关系条目（双向表示：存储在发起方角色中）</summary>
public class CharacterRelationship
{
    public string TargetId { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Relation { get; set; } = "";   // 如"父亲""朋友""敌人"等
    public string Note { get; set; } = "";        // 可选的备注说明
}

using System.Collections.Generic;

namespace XlaBoot;

/// <summary>Readable names for SoC model codes, so a bug report says "Snapdragon 8 Elite" and not just "SM8750".</summary>
public static class DeviceInfo
{
    private static readonly Dictionary<string, string> Snapdragon = new()
    {
        ["SM8850"] = "Snapdragon 8 Elite Gen 5",
        ["SM8750"] = "Snapdragon 8 Elite",
        ["SM8650"] = "Snapdragon 8 Gen 3",
        ["SM8635"] = "Snapdragon 8s Gen 3",
        ["SM8550"] = "Snapdragon 8 Gen 2",
        ["SM8475"] = "Snapdragon 8+ Gen 1",
        ["SM8450"] = "Snapdragon 8 Gen 1",
        ["SM8350"] = "Snapdragon 888",
        ["SM8250"] = "Snapdragon 865",
        ["SM7675"] = "Snapdragon 7+ Gen 3",
        ["SM7550"] = "Snapdragon 7 Gen 3",
    };

    /// <summary>" (Snapdragon 8 Elite)" for a known code, otherwise empty.</summary>
    public static string FriendlySoc(string? model) =>
        model != null && Snapdragon.TryGetValue(model.Trim().ToUpperInvariant(), out var name) ? $" ({name})" : "";
}

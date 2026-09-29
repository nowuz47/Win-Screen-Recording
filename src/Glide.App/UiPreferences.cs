using System.Text.Json;
using Glide.Core;

namespace Glide.App;

internal enum UiMotion { Standard, Reduced }

// App-only settings: never stored in a recording project or passed to the renderer.
internal sealed class UiPreferences
{
    private readonly Dictionary<string, JsonElement> values;
    public int Theme { get; set; }
    public UiMotion Motion { get; set; }
    private UiPreferences(Dictionary<string, JsonElement> values)
    {
        this.values = values;
        Theme = ReadInt("theme", 0, 2);
        Motion = (UiMotion)ReadInt("uiMotion", 0, 1);
    }
    private int ReadInt(string key, int min, int max) =>
        values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= min && number <= max ? number : min;
    public static UiPreferences Parse(string json) => new(
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new());
    public static UiPreferences Load(string path) => Parse(File.Exists(path) ? File.ReadAllText(path) : "{}");
    public void Save(string path)
    {
        var next = new Dictionary<string, JsonElement>(values)
        {
            ["theme"] = JsonSerializer.SerializeToElement(Math.Clamp(Theme, 0, 2)),
            ["uiMotion"] = JsonSerializer.SerializeToElement(Motion == UiMotion.Reduced ? 1 : 0)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFile.Replace(path, JsonSerializer.SerializeToUtf8Bytes(next));
    }
    public static bool ReducesMotion(UiMotion preference, bool systemAnimations) =>
        preference == UiMotion.Reduced || !systemAnimations;
}

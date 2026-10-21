using System.Text.Json;

namespace D2.GodotRef.Tests;

/// <summary>
/// Сторож для tools/godot-ref/godot47.jsonl: если после обновления Godot справочник
/// не пересобрали (или пересборка сломалась), эти проверки падают.
/// </summary>
public class GodotRefTests
{
    private static readonly Dictionary<string, JsonElement> Db = Load();

    private static Dictionary<string, JsonElement> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "tools", "godot-ref", "godot47.jsonl")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "tools", "godot-ref", "godot47.jsonl");
        return File.ReadLines(path)
            .Select(l => JsonDocument.Parse(l).RootElement)
            .ToDictionary(e => e.GetProperty("n").GetString()!, e => e);
    }

    private static IEnumerable<JsonElement> Items(string cls, string key) =>
        Db[cls].TryGetProperty(key, out var arr) ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string Name(JsonElement e) => e.GetProperty("n").GetString()!;

    [Fact]
    public void База_загружается_и_содержит_версию_движка()
    {
        Assert.True(Db.Count > 900, $"классов всего {Db.Count}");
        string version = Db["@meta"].GetProperty("b").GetString()!;
        Assert.StartsWith("Godot Engine v4.7", version);
    }

    [Fact]
    public void Sprite2D_наследует_Node2D_и_имеет_свойство_texture()
    {
        Assert.Equal("Node2D", Db["Sprite2D"].GetProperty("i").GetString());
        var texture = Items("Sprite2D", "p").Single(p => Name(p) == "texture");
        Assert.Equal("Texture2D", texture.GetProperty("t").GetString());
    }

    [Fact]
    public void Цепочка_наследования_Sprite2D_доходит_до_Object()
    {
        var chain = new List<string>();
        for (string? c = "Sprite2D"; c != null; c = Db[c].TryGetProperty("i", out var i) ? i.GetString() : null)
        {
            chain.Add(c);
        }

        Assert.Equal(new[] { "Sprite2D", "Node2D", "CanvasItem", "Node", "Object" }, chain);
    }

    [Fact]
    public void Image_умеет_грузить_png()
    {
        var names = Items("Image", "m").Select(Name).ToList();
        Assert.Contains("load", names);
        Assert.Contains("load_png_from_buffer", names);
        Assert.Contains("save_png", names);
    }

    [Fact]
    public void Node_add_child_принимает_Node_и_имеет_описание()
    {
        var m = Items("Node", "m").Single(x => Name(x) == "add_child");
        Assert.StartsWith("Node node", m.GetProperty("a").GetString());
        Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("d").GetString()));
    }

    [Fact]
    public void Встроенные_типы_и_глобальные_перечисления_на_месте()
    {
        Assert.Equal("builtin", Db["Vector2"].GetProperty("k").GetString());
        Assert.Equal("enum", Db["Error"].GetProperty("k").GetString());
        Assert.Equal("singleton", Db["Engine"].GetProperty("k").GetString());
    }
}

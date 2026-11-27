using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrawingHelper.Core.Progress;

public sealed class ProjectProgress
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("selected_tile")]
    public int[] SelectedTile { get; set; } = [0, 0];

    [JsonPropertyName("completed_tiles")]
    public List<int[]> CompletedTiles { get; set; } = [];

    [JsonPropertyName("completed_colors")]
    public Dictionary<string, List<int>> CompletedColors { get; set; } = [];
}

public static class ProgressStore
{
    public const long MaxProgressFileBytes = 10L * 1024 * 1024; // 10 MB cap

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        MaxDepth = 16
    };

    public static void Save(string path, ProjectProgress progress)
    {
        string json = JsonSerializer.Serialize(progress, JsonOpts);
        File.WriteAllText(path, json);
    }

    public static ProjectProgress Load(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists)
            throw new FileNotFoundException("진행 파일을 찾을 수 없습니다.", path);
        if (fi.Length > MaxProgressFileBytes)
            throw new InvalidOperationException("진행 파일 크기가 10MB를 초과합니다.");

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ProjectProgress>(json, JsonOpts)
            ?? new ProjectProgress();
    }
}

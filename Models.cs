using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Globalization;
using System.Text.RegularExpressions;

namespace YoloDatasetStudio;

public sealed class DatasetProject
{
    public string Name { get; set; } = "Новый датасет";
    public string Root { get; set; } = "";
    public List<DatasetClass> Classes { get; set; } = [];
    public int TrainPercent { get; set; } = 70;
    public int ValidationPercent { get; set; } = 20;
    public int TestPercent { get; set; } = 10;
    [JsonIgnore] public int TotalPercent => TrainPercent + ValidationPercent + TestPercent;
}

public sealed class DatasetClass
{
    public int Index { get; set; }
    public string Name { get; set; } = "object";
    public override string ToString() => $"{Index}: {Name}";
}

public sealed class Annotation
{
    public int ClassIndex { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public override string ToString() => $"Класс {ClassIndex}: ({CenterX:0.000}, {CenterY:0.000}) {Width:0.000} × {Height:0.000}";
}

public static class DatasetStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static string Images(string root) => Path.Combine(root, "images");
    public static string Labels(string root) => Path.Combine(root, "labels");
    public static string ProjectFile(string root) => Path.Combine(root, "dataset.json");

    public static void Save(DatasetProject project)
    {
        Directory.CreateDirectory(project.Root);
        Directory.CreateDirectory(Images(project.Root));
        Directory.CreateDirectory(Labels(project.Root));
        File.WriteAllText(ProjectFile(project.Root), JsonSerializer.Serialize(project, Options));
        var yaml = $"path: \"{project.Root.Replace("\\", "/")}\"\ntrain: images/train\nval: images/val\ntest: images/test\nnames:\n" +
            string.Join(Environment.NewLine, project.Classes.OrderBy(x => x.Index).Select(x => $"  {x.Index}: {x.Name}"));
        File.WriteAllText(Path.Combine(project.Root, "data.yaml"), yaml);
    }

    public static DatasetProject Load(string root)
    {
        var project = JsonSerializer.Deserialize<DatasetProject>(File.ReadAllText(ProjectFile(root)), Options) ?? new DatasetProject();
        project.Root = root;
        return project;
    }

    public static List<Annotation> ReadLabels(string file)
    {
        if (!File.Exists(file)) return [];
        return File.ReadAllLines(file).Where(x => !string.IsNullOrWhiteSpace(x)).Select(line =>
        {
            var clean = Regex.Replace(line, "\\u001b\\[[0-9;]*m", "");
            var parts = clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cls)) return null;
            var values = parts.Skip(1).Take(4).Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? (double?)value : null).ToArray();
            return values.Length == 4 && values.All(x => x.HasValue) ? new Annotation { ClassIndex = cls, CenterX = values[0]!.Value, CenterY = values[1]!.Value, Width = values[2]!.Value, Height = values[3]!.Value } : null;
        }).Where(x => x is not null).Cast<Annotation>().ToList();
    }

    public static int SanitizeLabels(string root)
    {
        var changed = 0;
        var labels = Labels(root);
        if (!Directory.Exists(labels)) return 0;
        foreach (var file in Directory.EnumerateFiles(labels, "*.txt", SearchOption.AllDirectories))
        {
            var valid = ReadLabels(file);
            var lines = valid.Select(a => $"{a.ClassIndex} {a.CenterX.ToString("0.000000", CultureInfo.InvariantCulture)} {a.CenterY.ToString("0.000000", CultureInfo.InvariantCulture)} {a.Width.ToString("0.000000", CultureInfo.InvariantCulture)} {a.Height.ToString("0.000000", CultureInfo.InvariantCulture)}").ToList();
            var original = File.ReadAllLines(file);
            if (!original.SequenceEqual(lines)) { File.WriteAllLines(file, lines); changed++; }
        }
        return changed;
    }

    public static void WriteLabels(string file, IEnumerable<Annotation> annotations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, annotations.Select(a => $"{a.ClassIndex} {a.CenterX.ToString("0.000000", CultureInfo.InvariantCulture)} {a.CenterY.ToString("0.000000", CultureInfo.InvariantCulture)} {a.Width.ToString("0.000000", CultureInfo.InvariantCulture)} {a.Height.ToString("0.000000", CultureInfo.InvariantCulture)}"));
    }
}

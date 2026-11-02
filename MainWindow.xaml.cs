using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Text.Json;
using Microsoft.VisualBasic;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace YoloDatasetStudio;

public partial class MainWindow : Window
{
    private DatasetProject? _project;
    private readonly ObservableCollection<FileInfo> _images = [];
    private readonly ObservableCollection<Annotation> _annotations = [];
    private readonly PythonBridge _python = new();
    private List<Annotation> _undo = [];
    private Point? _drawStart;
    private Annotation? _dragAnnotation;
    private Point _dragOffset;
    private Point _cursor;
    private string _selectedModel = "yolov8n.pt";
    private CancellationTokenSource? _operationCancellation;

    public MainWindow() { InitializeComponent(); ImagesList.ItemsSource = _images; AnnotationsGrid.ItemsSource = _annotations; ModelBox.SelectionChanged += ModelBox_SelectionChanged; ModelInfo.Text = _selectedModel; }

    private void CreateDataset_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку для нового датасета", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var name = Interaction.InputBox("Название датасета:", "Новый датасет", "my_dataset");
        var classText = Interaction.InputBox("Классы через ; в формате индекс=имя (например 0=drone;1=person;2=car):", "Классы", "0=drone;1=person");
        var classes = classText.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split('=', 2)).Where(x => x.Length == 2 && int.TryParse(x[0].Trim(), out _)).Select(x => new DatasetClass { Index = int.Parse(x[0].Trim()), Name = x[1].Trim() }).Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        if (classes.Count == 0) { MessageBox.Show("Нужно указать хотя бы один класс."); return; }
        _project = new DatasetProject { Name = name, Root = Path.Combine(dialog.SelectedPath, name), Classes = classes };
        DatasetStore.Save(_project); RefreshImages(); Status.Content = "Датасет создан";
    }

    private void UpdateOperationProgress(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            if (!json.RootElement.TryGetProperty("event", out var eventType) || eventType.GetString() != "progress") return;
            var percent = json.RootElement.GetProperty("percent").GetDouble();
            var current = json.RootElement.GetProperty("current").GetInt32();
            var total = json.RootElement.GetProperty("total").GetInt32();
            OperationProgress.Value = Math.Clamp(percent, 0, 100);
            var kind = json.RootElement.GetProperty("kind").GetString() == "train" ? "эпох" : "файлов";
            Details.Content = $"{percent:0.#}% — {current}/{total} {kind}; модель: {Path.GetFileName(SelectedModel())}; устройство: {SelectedDevice()}";
        }
        catch (JsonException) { }
    }

    private string SelectedDevice()
    {
        var value = (DeviceBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "CPU";
        return value.Equals("GPU", StringComparison.OrdinalIgnoreCase) ? "0" : value.Equals("Auto", StringComparison.OrdinalIgnoreCase) ? "auto" : "cpu";
    }

    private void ConfidenceSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConfidenceInfo is not null) ConfidenceInfo.Text = $"{e.NewValue:0}%";
    }

    private void OpenDataset_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку датасета", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || !File.Exists(DatasetStore.ProjectFile(dialog.SelectedPath))) return;
        _project = DatasetStore.Load(dialog.SelectedPath); RefreshImages(); Status.Content = $"Открыт: {_project.Name}";
    }

    private void AddImages_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { MessageBox.Show("Сначала создайте или откройте датасет."); return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp;*.webp", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var source = DatasetStore.Images(_project.Root); Directory.CreateDirectory(source);
        foreach (var file in dialog.FileNames) File.Copy(file, Path.Combine(source, Path.GetFileName(file)), true);
        RefreshImages();
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { MessageBox.Show("Сначала создайте или откройте датасет."); return; }
        using var dialog = new Forms.FolderBrowserDialog { Description = "Выберите папку с изображениями", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var target = DatasetStore.Images(_project.Root); Directory.CreateDirectory(target);
        var files = Directory.EnumerateFiles(dialog.SelectedPath, "*.*", SearchOption.AllDirectories).Where(IsImage).ToList();
        foreach (var file in files)
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination)) destination = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(file)}_{Guid.NewGuid():N}{Path.GetExtension(file)}");
            File.Copy(file, destination);
        }
        RefreshImages(); Status.Content = $"Добавлено изображений: {files.Count}";
    }

    private void Previous_Click(object sender, RoutedEventArgs e) { if (ImagesList.SelectedIndex > 0) ImagesList.SelectedIndex--; }
    private void Next_Click(object sender, RoutedEventArgs e) { if (ImagesList.SelectedIndex + 1 < ImagesList.Items.Count) ImagesList.SelectedIndex++; }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var textInput = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
        if (e.Key == Key.Left && !textInput) { Previous_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right && !textInput) { Next_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Delete && AnnotationsGrid.SelectedItem is Annotation) { DeleteAnnotation_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { ApplyAnnotations_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo_Click(sender, e); e.Handled = true; }
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        var values = Interaction.InputBox("Проценты train;validation;test (например 70;20;10):", "Распределение", $"{_project.TrainPercent};{_project.ValidationPercent};{_project.TestPercent}").Split(';');
        if (values.Length != 3 || !values.All(x => int.TryParse(x, out _))) { MessageBox.Show("Введите три целых числа."); return; }
        _project.TrainPercent = int.Parse(values[0]); _project.ValidationPercent = int.Parse(values[1]); _project.TestPercent = int.Parse(values[2]);
        if (_project.TotalPercent != 100) { MessageBox.Show("Сумма процентов должна быть 100."); return; }
        var files = _images.OrderBy(_ => Guid.NewGuid()).ToList(); var boundaries = new[] { _project.TrainPercent, _project.TrainPercent + _project.ValidationPercent };
        for (var i = 0; i < files.Count; i++) { var percent = (i * 100.0) / Math.Max(1, files.Count); var split = percent < boundaries[0] ? "train" : percent < boundaries[1] ? "val" : "test"; var imageTarget = Path.Combine(_project.Root, "images", split, files[i].Name); var label = Path.ChangeExtension(files[i].FullName.Replace("images", "labels"), ".txt"); var labelTarget = Path.Combine(_project.Root, "labels", split, Path.ChangeExtension(files[i].Name, ".txt")); Directory.CreateDirectory(Path.GetDirectoryName(imageTarget)!); Directory.CreateDirectory(Path.GetDirectoryName(labelTarget)!); File.Copy(files[i].FullName, imageTarget, true); if (File.Exists(label)) File.Copy(label, labelTarget, true); }
        DatasetStore.Save(_project); RefreshImages(); Status.Content = "Распределение завершено";
    }

    private void RefreshImages()
    {
        _images.Clear(); if (_project is null) return;
        Directory.CreateDirectory(DatasetStore.Images(_project.Root));
        foreach (var file in Directory.EnumerateFiles(DatasetStore.Images(_project.Root), "*.*", SearchOption.AllDirectories).Where(IsImage)) _images.Add(new FileInfo(file));
    }
    private static bool IsImage(string x) => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Contains(Path.GetExtension(x).ToLowerInvariant());

    private void ImagesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ImagesList.SelectedItem is not FileInfo file) return;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(file.FullName); image.EndInit(); Preview.Source = image; ImageInfo.Text = file.Name;
        _annotations.Clear(); foreach (var item in DatasetStore.ReadLabels(Path.ChangeExtension(file.FullName.Replace("images", "labels"), ".txt"))) _annotations.Add(item);
        _undo = _annotations.Select(Clone).ToList();
        DrawOverlay();
    }

    private string SelectedModel()
    {
        if (!string.IsNullOrWhiteSpace(_selectedModel)) return _selectedModel;
        return ((ComboBoxItem)ModelBox.SelectedItem).Content.ToString() ?? "yolov8n.pt";
    }

    private void ModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelBox.SelectedItem is ComboBoxItem item) { _selectedModel = item.Content.ToString() ?? "yolov8n.pt"; ModelInfo.Text = _selectedModel; }
    }

    private void ChooseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Модели YOLO (*.pt)|*.pt", Title = "Выберите модель YOLOv8" };
        if (dialog.ShowDialog() == true) { _selectedModel = dialog.FileName; ModelInfo.Text = dialog.FileName; Status.Content = "Выбрана пользовательская модель"; }
    }

    private void ChooseTrainedModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Лучшая модель (*.pt)|*.pt", Title = "Выберите best.pt после обучения" };
        if (dialog.ShowDialog() == true) { _selectedModel = dialog.FileName; ModelInfo.Text = dialog.FileName; Status.Content = "Выбрана обученная модель"; }
    }

    private void ApplyAnnotations_Click(object sender, RoutedEventArgs e)
    {
        if (ImagesList.SelectedItem is not FileInfo file) return;
        _undo = _annotations.Select(Clone).ToList(); DatasetStore.WriteLabels(Path.ChangeExtension(file.FullName.Replace("images", "labels"), ".txt"), _annotations); Status.Content = "Разметка сохранена";
        DrawOverlay();
    }
    private void DeleteAnnotation_Click(object sender, RoutedEventArgs e) { if (AnnotationsGrid.SelectedItem is Annotation a) { _undo = _annotations.Select(Clone).ToList(); _annotations.Remove(a); DrawOverlay(); } }
    private void Undo_Click(object sender, RoutedEventArgs e) { _annotations.Clear(); foreach (var a in _undo.Select(Clone)) _annotations.Add(a); DrawOverlay(); }
    private static Annotation Clone(Annotation a) => new() { ClassIndex = a.ClassIndex, CenterX = a.CenterX, CenterY = a.CenterY, Width = a.Width, Height = a.Height };

    private Rect ImageRect()
    {
        if (Preview.Source is not BitmapSource source || Overlay.ActualWidth <= 0 || Overlay.ActualHeight <= 0) return Rect.Empty;
        var scale = Math.Min(Overlay.ActualWidth / source.PixelWidth, Overlay.ActualHeight / source.PixelHeight);
        var width = source.PixelWidth * scale; var height = source.PixelHeight * scale;
        return new Rect((Overlay.ActualWidth - width) / 2, (Overlay.ActualHeight - height) / 2, width, height);
    }

    private Point ToImagePoint(Point point)
    {
        var rect = ImageRect();
        if (rect.IsEmpty) return new Point(0, 0);
        return new Point(Math.Clamp((point.X - rect.Left) / rect.Width, 0, 1), Math.Clamp((point.Y - rect.Top) / rect.Height, 0, 1));
    }

    private Rect ToScreenRect(Annotation annotation)
    {
        var image = ImageRect();
        var left = image.Left + (annotation.CenterX - annotation.Width / 2) * image.Width;
        var top = image.Top + (annotation.CenterY - annotation.Height / 2) * image.Height;
        return new Rect(left, top, annotation.Width * image.Width, annotation.Height * image.Height);
    }

    private Annotation? AnnotationAt(Point point)
    {
        for (var i = _annotations.Count - 1; i >= 0; i--) if (ToScreenRect(_annotations[i]).Contains(point)) return _annotations[i];
        return null;
    }

    private void DrawOverlay()
    {
        if (Overlay is null) return;
        Overlay.Children.Clear();
        var image = ImageRect();
        if (image.IsEmpty) return;
        foreach (var annotation in _annotations)
        {
            var rectangle = ToScreenRect(annotation);
            var shape = new Rectangle { Width = Math.Max(1, rectangle.Width), Height = Math.Max(1, rectangle.Height), Stroke = Brushes.LimeGreen, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, Fill = new SolidColorBrush(Color.FromArgb(25, 50, 205, 50)) };
            Canvas.SetLeft(shape, rectangle.Left); Canvas.SetTop(shape, rectangle.Top); Overlay.Children.Add(shape);
            var text = new TextBlock { Text = annotation.ClassIndex.ToString(), Foreground = Brushes.LimeGreen, Background = Brushes.Black, Padding = new Thickness(2, 0, 2, 0) };
            Canvas.SetLeft(text, rectangle.Left); Canvas.SetTop(text, Math.Max(image.Top, rectangle.Top - 18)); Overlay.Children.Add(text);
        }
        if (_drawStart is Point start)
        {
            var current = ToImagePoint(_cursor);
            var draft = new Annotation { CenterX = (start.X + current.X) / 2, CenterY = (start.Y + current.Y) / 2, Width = Math.Abs(current.X - start.X), Height = Math.Abs(current.Y - start.Y) };
            var rectangle = ToScreenRect(draft);
            var shape = new Rectangle { Width = Math.Max(1, rectangle.Width), Height = Math.Max(1, rectangle.Height), Stroke = Brushes.LimeGreen, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 } };
            Canvas.SetLeft(shape, rectangle.Left); Canvas.SetTop(shape, rectangle.Top); Overlay.Children.Add(shape);
        }
        var vertical = new Line { X1 = _cursor.X, X2 = _cursor.X, Y1 = image.Top, Y2 = image.Bottom, Stroke = Brushes.LimeGreen, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, IsHitTestVisible = false };
        var horizontal = new Line { X1 = image.Left, X2 = image.Right, Y1 = _cursor.Y, Y2 = _cursor.Y, Stroke = Brushes.LimeGreen, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 2 }, IsHitTestVisible = false };
        Overlay.Children.Add(vertical); Overlay.Children.Add(horizontal);
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        _cursor = e.GetPosition(Overlay);
        var imagePoint = ToImagePoint(_cursor);
        if (_dragAnnotation is not null)
        {
            _dragAnnotation.CenterX = Math.Clamp(imagePoint.X + _dragOffset.X, _dragAnnotation.Width / 2, 1 - _dragAnnotation.Width / 2);
            _dragAnnotation.CenterY = Math.Clamp(imagePoint.Y + _dragOffset.Y, _dragAnnotation.Height / 2, 1 - _dragAnnotation.Height / 2);
        }
        DrawOverlay();
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cursor = e.GetPosition(Overlay);
        var point = ToImagePoint(_cursor);
        if (_drawStart is Point start)
        {
            var width = Math.Abs(point.X - start.X); var height = Math.Abs(point.Y - start.Y);
            if (width >= 0.005 && height >= 0.005) _annotations.Add(new Annotation { ClassIndex = _project?.Classes.FirstOrDefault()?.Index ?? 0, CenterX = (start.X + point.X) / 2, CenterY = (start.Y + point.Y) / 2, Width = width, Height = height });
            _drawStart = null; DrawOverlay(); e.Handled = true; return;
        }
        if (_dragAnnotation is not null) { _dragAnnotation = null; DrawOverlay(); e.Handled = true; return; }
        var existing = AnnotationAt(_cursor);
        if (existing is not null) { _dragAnnotation = existing; _dragOffset = new Point(existing.CenterX - point.X, existing.CenterY - point.Y); }
        else _drawStart = point;
        DrawOverlay(); e.Handled = true;
    }

    private void Overlay_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _cursor = e.GetPosition(Overlay);
        if (_drawStart is not null) _drawStart = null;
        else if (AnnotationAt(_cursor) is Annotation annotation) { _undo = _annotations.Select(Clone).ToList(); _annotations.Remove(annotation); Status.Content = "Выделение удалено"; }
        DrawOverlay(); e.Handled = true;
    }

    private void Overlay_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOverlay();

    private void DeleteImage_Click(object sender, RoutedEventArgs e)
    {
        if (ImagesList.SelectedItem is not FileInfo file || MessageBox.Show("Удалить изображение и разметку?", "Подтверждение", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var label = Path.ChangeExtension(file.FullName.Replace("images", "labels"), ".txt"); file.Delete(); if (File.Exists(label)) File.Delete(label); RefreshImages();
    }

    private void DeleteUnlabeled_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) { MessageBox.Show("Сначала создайте или откройте датасет."); return; }
        var images = Directory.EnumerateFiles(DatasetStore.Images(_project.Root), "*.*", SearchOption.AllDirectories).Where(IsImage).ToList();
        var candidates = images.Where(image => !DatasetStore.ReadLabels(Path.ChangeExtension(image.Replace("images", "labels"), ".txt")).Any()).ToList();
        var orphanLabels = Directory.Exists(DatasetStore.Labels(_project.Root))
            ? Directory.EnumerateFiles(DatasetStore.Labels(_project.Root), "*.txt", SearchOption.AllDirectories).Where(label => !DatasetStore.ReadLabels(label).Any() && !ImageExistsForLabel(label)).ToList()
            : [];
        if (candidates.Count == 0 && orphanLabels.Count == 0) { MessageBox.Show("Пустых изображений и разметок не найдено."); return; }
        if (MessageBox.Show($"Будет удалено изображений: {candidates.Count}\nПустых txt без изображения: {orphanLabels.Count}\nПродолжить?", "Удаление без разметки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var image in candidates)
        {
            var label = Path.ChangeExtension(image.Replace("images", "labels"), ".txt");
            File.Delete(image); if (File.Exists(label)) File.Delete(label);
        }
        foreach (var label in orphanLabels) File.Delete(label);
        RefreshImages(); Status.Content = $"Удалено изображений без разметки: {candidates.Count}";
    }

    private bool ImageExistsForLabel(string label)
    {
        if (_project is null) return false;
        var relative = Path.GetRelativePath(DatasetStore.Labels(_project.Root), label);
        var imageBase = Path.Combine(DatasetStore.Images(_project.Root), Path.ChangeExtension(relative, null)!);
        return new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" }.Any(extension => File.Exists(imageBase + extension));
    }

    private async void Annotate_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        try { OperationProgress.Value = 0; Status.Content = $"Авторазметка: {SelectedDevice()}, порог {ConfidenceSlider.Value:0}%..."; await _python.RunAsync(new() { ["command"] = "annotate", ["model"] = SelectedModel(), ["source"] = DatasetStore.Images(_project.Root), ["output"] = DatasetStore.Labels(_project.Root), ["mode"] = ((ComboBoxItem)ModeBox.SelectedItem).Content.ToString()!, ["confidence"] = ConfidenceSlider.Value / 100.0, ["device"] = SelectedDevice(), ["image_size"] = 640 }, new Progress<string>(UpdateOperationProgress), _operationCancellation.Token); RefreshImages(); if (ImagesList.SelectedItem is not null) ImagesList_SelectionChanged(ImagesList, null!); OperationProgress.Value = 100; Status.Content = "Авторазметка завершена"; } catch (OperationCanceledException) { Status.Content = "Авторазметка остановлена"; } catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка авторазметки"); Status.Content = "Ошибка авторазметки"; } finally { _operationCancellation.Dispose(); _operationCancellation = null; }
    }

    private async void Train_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (!PrepareTrainingSplits()) return;
        if (!int.TryParse(EpochsBox.Text, out var epochs) || epochs < 1 || epochs > 10000) { MessageBox.Show("Количество эпох должно быть от 1 до 10000.", "Проверка"); EpochsBox.Focus(); return; }
        DatasetStore.Save(_project);
        if (_operationCancellation is not null) return;
        _operationCancellation = new CancellationTokenSource();
        try { Status.Content = $"Обучение: {SelectedDevice()}..."; var output = await _python.RunAsync(new() { ["command"] = "train", ["model"] = SelectedModel(), ["data"] = Path.Combine(_project.Root, "data.yaml"), ["project"] = Path.Combine(_project.Root, "runs"), ["name"] = "training", ["device"] = SelectedDevice(), ["epochs"] = epochs, ["batch"] = 1, ["image_size"] = 640 }, cancellationToken: _operationCancellation.Token); var runs = Path.Combine(_project.Root, "runs"); var best = Directory.Exists(runs) ? Directory.EnumerateFiles(runs, "best.pt", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null; if (best is not null) { _selectedModel = best; ModelInfo.Text = best; MessageBox.Show($"Обучение завершено. Выбрана лучшая модель:\n{best}", "Обучение"); } else MessageBox.Show(output, "Обучение завершено"); Status.Content = "Обучение завершено"; } catch (OperationCanceledException) { Status.Content = "Обучение остановлено"; } catch (Exception ex) { MessageBox.Show(ex.Message, "Ошибка обучения"); Status.Content = "Ошибка обучения"; } finally { _operationCancellation.Dispose(); _operationCancellation = null; }
    }

    private void StopOperation_Click(object sender, RoutedEventArgs e) { _operationCancellation?.Cancel(); Status.Content = "Остановка операции..."; }

    private bool PrepareTrainingSplits()
    {
        if (_project is null) return false;
        var cleaned = DatasetStore.SanitizeLabels(_project.Root);
        if (cleaned > 0) Status.Content = $"Исправлено label-файлов: {cleaned}";
        var imagesRoot = DatasetStore.Images(_project.Root);
        var train = Directory.Exists(Path.Combine(imagesRoot, "train"));
        var val = Directory.Exists(Path.Combine(imagesRoot, "val"));
        if (train && val) return true;
        var sourceFiles = Directory.EnumerateFiles(imagesRoot, "*.*", SearchOption.TopDirectoryOnly).Where(IsImage).OrderBy(_ => Guid.NewGuid()).ToList();
        if (sourceFiles.Count < 2) { MessageBox.Show("Для обучения нужны минимум 2 изображения. Добавьте изображения и повторите."); return false; }
        var trainCount = Math.Clamp((int)Math.Round(sourceFiles.Count * _project.TrainPercent / 100.0), 1, sourceFiles.Count - 1);
        for (var i = 0; i < sourceFiles.Count; i++)
        {
            var split = i < trainCount ? "train" : "val";
            var imageTarget = Path.Combine(imagesRoot, split, Path.GetFileName(sourceFiles[i]));
            var labelSource = Path.ChangeExtension(sourceFiles[i].Replace("images", "labels"), ".txt");
            var labelTarget = Path.Combine(_project.Root, "labels", split, Path.ChangeExtension(Path.GetFileName(sourceFiles[i]), ".txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(imageTarget)!); Directory.CreateDirectory(Path.GetDirectoryName(labelTarget)!);
            File.Copy(sourceFiles[i], imageTarget, true);
            if (File.Exists(labelSource)) File.Copy(labelSource, labelTarget, true);
        }
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) { if (_project is not null) { DatasetStore.Save(_project); ApplyAnnotations_Click(sender, e); Status.Content = "Сохранено"; } }
}

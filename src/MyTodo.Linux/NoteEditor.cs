using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace MyTodo.Linux;

// Plain text remains interoperable with Windows. Original FlowDocument XML is
// preserved until an intentional edit; it is parsed as XML, never instantiated.
public sealed class NoteEditor : UserControl
{
    private readonly TextBox _input;
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 110 };
    private readonly StackPanel _root = new() { Spacing = 8 };
    public event Action<string>? Changed;
    private static readonly Regex Links = new(@"\[(?<label>[^\]\r\n]+)\]\((?<url>[^)\r\n]+)\)|(?<url>https?://[^\s<>]+|file:///[^\s<>]+|/(?:[^\s<>]+))", RegexOptions.Compiled);
    public NoteEditor(string text, string xaml)
    {
        var legacy = ExtractText(xaml);
        _input = new TextBox { Text = !string.IsNullOrWhiteSpace(legacy) ? legacy : text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150 };
        _input.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) { Changed?.Invoke(_input.Text ?? ""); Render(); } };
        _input.IsVisible = false;
        _input.LostFocus += (_, _) => { _input.IsVisible = false; _preview.IsVisible = true; };
        _preview.DoubleTapped += (_, e) => { _preview.IsVisible = false; _input.IsVisible = true; _input.Focus(); e.Handled = true; };
        var edit = new MenuItem { Header = "编辑备注" };
        edit.Click += (_, _) => { _preview.IsVisible = false; _input.IsVisible = true; _input.Focus(); };
        _preview.ContextMenu = new ContextMenu { ItemsSource = new[] { edit } };
        var link = new MenuItem { Header = "设为超链接" };
        link.Click += (_, _) =>
        {
            var selected = _input.SelectedText;
            if (!string.IsNullOrWhiteSpace(selected)) _input.SelectedText = $"[{selected}]({selected})";
        };
        _input.ContextMenu = new ContextMenu { ItemsSource = new[] { link } };
        _root.Children.Add(_input);
        _root.Children.Add(new Border { Child = _preview, BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Padding = new Thickness(12) });
        Content = _root;
        Render();
    }
    public static string ExtractText(string xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml)) return "";
        try
        {
            var xml = XDocument.Parse(xaml);
            string Read(XNode node)
            {
                if (node is XText t) return t.Value;
                if (node is not XElement element) return "";
                if (element.Name.LocalName == "LineBreak") return "\n";
                var value = string.Concat(element.Nodes().Select(Read));
                var address = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "NavigateUri")?.Value;
                return address == null ? value : $"[{value}]({address})";
            }
            return string.Join("\n", xml.Descendants().Where(x => x.Name.LocalName == "Paragraph").Select(Read));
        }
        catch (System.Xml.XmlException) { return ""; }
    }
    private void Render()
    {
        _preview.Inlines!.Clear();
        var text = _input.Text ?? "";
        int start = 0;
        foreach (Match match in Links.Matches(text))
        {
            _preview.Inlines.Add(new Run(text[start..match.Index]));
            var url = match.Groups["url"].Value.TrimEnd('。', '，', ',', ';');
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : url;
            var control = new TextBlock { Text = label, Foreground = Brushes.DodgerBlue, TextDecorations = TextDecorations.Underline, Cursor = new Cursor(StandardCursorType.Hand) };
            control.DoubleTapped += (_, e) => { Open(url); e.Handled = true; };
            ToolTip.SetTip(control, url);
            _preview.Inlines.Add(new InlineUIContainer(control));
            start = match.Index + match.Length;
        }
        _preview.Inlines.Add(new Run(text.Length == 0 ? "…" : text[start..]));
    }
    public static void Open(string target)
    {
        // Do not launch arbitrary URI handlers or interpret shell syntax.
        if (!Path.IsPathFullyQualified(target) && !(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "file")) return;
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine(e.Message); }
    }
}

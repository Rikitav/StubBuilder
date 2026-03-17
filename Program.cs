using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace IncludeGraphScanner;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Scanner usage :\nscanner.exe {includes directory} {output file name}");
            return;
        }

        if (!Directory.Exists(args[0]))
        {
            Console.WriteLine("Includes directory not found");
            return;
        }

        DirectoryInfo currentDirectory = new DirectoryInfo(args[0]);
        IncludeGraph graph = new IncludeGraph(currentDirectory.FullName);

        foreach (FileInfo header in currentDirectory.GetFiles("*.hpp", SearchOption.AllDirectories))
            graph.AddHeader(header);

        HeaderBuilder builder = new HeaderBuilder();
        foreach (IncludeNode node in graph)
            builder.Append(node);

        string stub = builder.ToString();
        File.WriteAllText(args[1], stub);
    }
}

public partial class HeaderBuilder()
{
    private readonly StringBuilder _headerBuilder = new StringBuilder();

    public void Append(IncludeNode node)
    {
        if (!node.File.Exists)
            throw new FileNotFoundException(node.File.FullName);

        string content = File.ReadAllText(node.File.FullName);
        StringBuilder stringBuilder = new StringBuilder(content);

        foreach (Match match in PragmaDirectiveRegex().Matches(content))
            stringBuilder.Replace(match.Groups[0].Value, string.Empty);

        foreach (string directive in node.Directives)
            stringBuilder.Replace(directive, string.Empty);

        _headerBuilder.Append("// ");
        _headerBuilder.AppendLine(node.File.FullName);
        _headerBuilder.Append(stringBuilder);
        _headerBuilder.AppendLine();
    }

    public override string ToString()
    {
        return MultipleNewlinesRegex().Replace(_headerBuilder.ToString(), Environment.NewLine + Environment.NewLine);
    }

    [GeneratedRegex(@"(\r\n|\n){3,}", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex MultipleNewlinesRegex();

    [GeneratedRegex(@"^\s*#pragma\s+once\s*", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex PragmaDirectiveRegex();
}

public record class IncludeNode(FileInfo File)
{
    public List<string> Directives { get; } = [];
    public List<IncludeNode> Dependencies { get; } = [];
}

public partial class IncludeGraph(string includeRoot) : IEnumerable<IncludeNode>
{
    private enum IncludeState
    {
        Unvisited,
        Visiting,
        Visited
    }

    private readonly static HashSet<string> _systemIncludes = File.Exists("system_headers.csv")
        ? new HashSet<string>(File.ReadAllText("system_headers.csv").Split(",", StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase) : [];

    private readonly Dictionary<FileInfo, IncludeNode> _nodes = new Dictionary<FileInfo, IncludeNode>(new FileInfoComparer());
    private readonly List<string> _includeRoots = [includeRoot];

    public IncludeNode AddHeader(FileInfo file)
    {
        file = new FileInfo(Path.GetFullPath(file.FullName));
        if (_nodes.TryGetValue(file, out IncludeNode? existingNode))
            return existingNode;

        IncludeNode node = new IncludeNode(file);
        _nodes.Add(file, node);

        if (!file.Exists)
        {
            ColorLine(ConsoleColor.Yellow, $"[Warning] File not found : '{file.Name}'");
            return node;
        }

        ColorLine(ConsoleColor.Cyan, $"[Info] File Added : '{file.Name}'");
        ParseDependencies(node);
        return node;
    }

    private void ParseDependencies(IncludeNode node)
    {
        string content = File.ReadAllText(node.File.FullName);
        var matches = IncludeDirectiveRegex().Matches(content);

        foreach (Match match in matches)
        {
            string includedFileName = match.Groups[1].Value;
            if (_systemIncludes.Contains(includedFileName, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(includedFileName))
            {
                bool found = false;
                foreach (string includeRoot in _includeRoots)
                {
                    string resolvedPath = Path.Combine(includeRoot, match.Groups[1].Value);
                    if (File.Exists(resolvedPath))
                    {
                        includedFileName = resolvedPath;
                        found = true;
                    }
                }

                if (!found)
                {
                    ColorLine(ConsoleColor.Yellow, $"[Warning] File not found : '{match.Groups[1].Value}'");
                    continue;
                }    
            }

            IncludeNode depNode = AddHeader(new FileInfo(includedFileName));
            node.Directives.Add(match.Groups[0].Value);
            node.Dependencies.Add(depNode);
        }
    }

    public IEnumerator<IncludeNode> GetEnumerator()
    {
        List<IncludeNode> sortedList = [];
        Dictionary<IncludeNode, IncludeState> states = [];

        foreach (IncludeNode node in _nodes.Values)
        {
            if (!states.TryGetValue(node, out IncludeState value) || value == IncludeState.Unvisited)
                Visit(node, states, sortedList);
        }

        return sortedList.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static void Visit(IncludeNode node, Dictionary<IncludeNode, IncludeState> states, List<IncludeNode> sortedList)
    {
        states[node] = IncludeState.Visiting;
        foreach (IncludeNode dep in node.Dependencies)
        {
            if (!states.TryGetValue(dep, out IncludeState state) || state == IncludeState.Unvisited)
            {
                Visit(dep, states, sortedList);
                continue;
            }

            if (state == IncludeState.Visiting)
            {
                ColorLine(ConsoleColor.Red, $"Detected cycling dependency! '{node.File.Name}' is trying to include '{dep.File.Name}', which is already being visiting.");
                Environment.ExitCode = 1;
            }
        }

        states[node] = IncludeState.Visited;
        sortedList.Add(node);
    }

    private static void ColorLine(ConsoleColor color, string content)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(content);
        Console.ResetColor();
    }

    [GeneratedRegex(@"^\s*#include\s*[""<](.+)["">]", RegexOptions.Multiline | RegexOptions.Compiled)]
    private static partial Regex IncludeDirectiveRegex();
}

public class FileInfoComparer : IEqualityComparer<FileInfo>
{
    public bool Equals(FileInfo? left, FileInfo? right)
    {
        if (left == null && right == null)
            return true;

        if (left == null || right == null)
            return false;

        return left.FullName.Equals(right.FullName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode([DisallowNull] FileInfo obj)
    {
        return obj.FullName.GetHashCode();
    }
}

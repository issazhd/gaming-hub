using System.Text;

namespace GamingHub.Core.Discovery;

/// <summary>Minimal Valve KeyValues (text VDF/ACF) parser: nested objects of string values, case-insensitive keys.</summary>
public sealed class VdfNode
{
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public VdfNode? this[string key] => Children.GetValueOrDefault(key);
    public string? Get(string key) => Values.GetValueOrDefault(key);
}

public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int i = 0;
        var root = new VdfNode();
        ParseInto(root, text, ref i);
        return root;
    }

    static void ParseInto(VdfNode node, string s, ref int i)
    {
        while (true)
        {
            var key = Token(s, ref i);
            if (key is null || key == "}") return;
            var next = Token(s, ref i);
            if (next is null) return;
            if (next == "{")
            {
                var child = new VdfNode();
                ParseInto(child, s, ref i);
                node.Children[key] = child;
            }
            else node.Values[key] = next;
        }
    }

    static string? Token(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; return c.ToString(); }
            if (c == '"')
            {
                var sb = new StringBuilder();
                for (i++; i < s.Length && s[i] != '"'; i++)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] }); }
                    else sb.Append(s[i]);
                }
                i++;
                return sb.ToString();
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            return s[start..i];
        }
        return null;
    }
}

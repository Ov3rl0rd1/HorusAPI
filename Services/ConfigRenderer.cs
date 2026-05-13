using System.Text.RegularExpressions;

namespace HorusAPI.Services;

public static class ConfigRenderer
{
    // Pass 1: matches  #???varname\n...block...\n#???
    private static readonly Regex ConditionalBlock = new(
        @"#\?\?\?(\w+)\r?\n([\s\S]*?)#\?\?\?\r?\n?",
        RegexOptions.Compiled);

    // Pass 2: matches  #varname
    private static readonly Regex Variable = new(
        @"#(\w+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Renders a config template by resolving conditional blocks then substituting variables.
    /// Conditional syntax:  #???varname\n...block...\n#???
    ///   – block is included when vars[varname] is non-null/non-empty, removed otherwise.
    /// Variable syntax: #varname → vars[varname] ?? ""
    /// </summary>
    public static string Render(string template, Dictionary<string, string?> vars)
    {
        // Pass 1 – resolve conditional blocks
        string result = ConditionalBlock.Replace(template, match =>
        {
            string varName = match.Groups[1].Value;
            string block   = match.Groups[2].Value;

            return IsTruthy(vars.GetValueOrDefault(varName))
                ? block + "\n"
                : string.Empty;
        });

        // Pass 2 – substitute variables
        result = Variable.Replace(result, match =>
        {
            string varName = match.Groups[1].Value;
            return vars.TryGetValue(varName, out string? value) && value is not null
                ? value
                : string.Empty;
        });

        return result;
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrEmpty(value) && value != "false" && value != "0";
}
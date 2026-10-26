namespace Lounge.Core.Settings;

public enum SettingKind
{
    /// <summary>bool</summary>
    Toggle,
    /// <summary>int, stepped with D-pad left/right between Min and Max</summary>
    Integer,
    /// <summary>double, stepped with D-pad left/right between Min and Max</summary>
    Number,
    /// <summary>string, one of <see cref="SettingDescriptor.Choices"/></summary>
    Choice,
    /// <summary>string path picked in the file explorer ("" = default)</summary>
    Path,
    /// <summary>string[] of folders, added from the file explorer</summary>
    Folders,
    /// <summary>no value; the UI runs a command (open Remote, rescan, reset all)</summary>
    Action,
    /// <summary>string typed on the on-screen keyboard ("" = <see cref="SettingDescriptor.Empty"/>)</summary>
    Text,
}

/// <summary>Which file a setting lives in. Player settings shared with tivi's vocabulary
/// go to Lounge's own config.ini so the format matches tivi; everything else is JSON.</summary>
public enum SettingStore { Json, Ini }

public sealed record SettingChoice(string Value, string Label);

/// <summary>One adjustable setting. The settings UI is generated from these, so a
/// setting cannot exist without a row to change it.</summary>
public sealed record SettingDescriptor(
    string Key,
    string Section,
    string Label,
    SettingKind Kind,
    object? Default)
{
    public string Help { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;
    /// <summary>Suffix shown after the value: "%", " s", " ms", "x".</summary>
    public string Unit { get; init; } = "";
    public IReadOnlyList<SettingChoice> Choices { get; init; } = [];
    public SettingStore Store { get; init; } = SettingStore.Json;
    /// <summary>For hex-coloured ini values (tivi writes sub_color=ffffff).</summary>
    public bool IniHex { get; init; }
    /// <summary>Scale between the stored value and the shown one (tivi stores volume 0..1, the UI shows %).</summary>
    public double DisplayScale { get; init; } = 1;
    /// <summary>What an empty text setting shows.</summary>
    public string Empty { get; init; } = "None";

    public string ShowValue(object? value)
    {
        switch (Kind)
        {
            case SettingKind.Toggle: return value is true ? "On" : "Off";
            case SettingKind.Integer:
            case SettingKind.Number:
                var d = Convert.ToDouble(value ?? 0) * DisplayScale;
                var stepShown = Step * DisplayScale;
                var whole = Math.Abs(stepShown - Math.Round(stepShown)) < 1e-9;
                var text = whole ? Math.Round(d).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                                 : d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                return text + Unit;
            case SettingKind.Choice:
                var s = value as string ?? "";
                return Choices.FirstOrDefault(c => c.Value == s)?.Label ?? s;
            case SettingKind.Path:
                var p = value as string;
                return string.IsNullOrEmpty(p) ? "Default" : System.IO.Path.GetFileName(p.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : p;
            case SettingKind.Text:
                return value is string { Length: > 0 } text2 ? text2 : Empty;
            case SettingKind.Folders:
                var list = value as IReadOnlyList<string> ?? [];
                return list.Count switch { 0 => "None", 1 => "1 folder", _ => $"{list.Count} folders" };
            default:
                return "";
        }
    }
}

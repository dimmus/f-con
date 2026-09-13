namespace FCon.Abstractions.Plugins;

public enum FieldKind
{
    Text,
    Secret,
    Number,
    Toggle,
    Choice,
    Multiline,
    /// <summary>A UUID with a "generate" affordance in the editor.</summary>
    Uuid,
}

/// <summary>
/// Declarative description of one editable field. The GUI renders editors from these,
/// so a plugin never references any UI framework.
/// </summary>
/// <param name="Key">Key inside <see cref="Model.ProxyNode.Settings"/>.</param>
public sealed record FieldSpec(
    string Key,
    string Label,
    FieldKind Kind = FieldKind.Text)
{
    public string? Placeholder { get; init; }
    public string? Help { get; init; }
    public string? Default { get; init; }
    public bool Required { get; init; }
    /// <summary>Allowed values when <see cref="Kind"/> is <see cref="FieldKind.Choice"/>.</summary>
    public IReadOnlyList<ChoiceOption> Choices { get; init; } = [];
    /// <summary>Show this field only when <c>VisibleWhenKey</c> holds one of <c>VisibleWhenValues</c>.</summary>
    public string? VisibleWhenKey { get; init; }
    public IReadOnlyList<string> VisibleWhenValues { get; init; } = [];
    public int? Min { get; init; }
    public int? Max { get; init; }
}

public sealed record ChoiceOption(string Value, string Label)
{
    public static ChoiceOption Of(string value) => new(value, value);
}

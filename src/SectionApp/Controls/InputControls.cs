using Avalonia;
using Avalonia.Controls.Primitives;

namespace SectionApp.Controls;

/// <summary>Property row of the inputs column: the name with its unit on the left, the value on the right.</summary>
public class Field : HeaderedContentControl
{
    public static readonly StyledProperty<double> ValueWidthProperty = AvaloniaProperty.Register<Field, double>(nameof(ValueWidth), 108);

    /// <summary>Width of the value column; the rows of one group align on it.</summary>
    public double ValueWidth { get => GetValue(ValueWidthProperty); set => SetValue(ValueWidthProperty, value); }
}

/// <summary>Collapsible group of the inputs column; the header may hold a switch (a check box) of its own.</summary>
public class Group : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<Group, bool>(nameof(IsExpanded), true,
        defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
}

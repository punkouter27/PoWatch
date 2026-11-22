using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace PoWatch.Client.Layout;

/// <summary>
/// One option of a string-keyed <c>RadzenDropDown</c>: its label, tagged with the key (<c>data-value</c>)
/// so tests and styles can find an option by what it means rather than by its wording. Radzen hands
/// templates a <c>dynamic</c> item; callers pass it as <c>(object)item</c> so nothing binds at run time
/// (a trimmed build cannot afford the dynamic binder).
/// </summary>
public static class PickOption
{
    public static RenderFragment Render(object item, Func<string, string> label) => builder =>
    {
        var key = item as string ?? string.Empty;
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "data-value", key);
        builder.AddContent(2, label(key));
        builder.CloseElement();
    };

    /// <summary>The selected value's text, without the tag (the dropdown shows it in its own box).</summary>
    public static string Text(object item, Func<string, string> label) => label(item as string ?? string.Empty);
}

using System.Diagnostics.CodeAnalysis;

namespace PoWatch.Client;

public partial class App
{
    // The Router's NotFoundPage receives typeof(Layout.NotFound) through generated code the trim
    // analyzer can't follow (IL2111 is suppressed in the csproj), so the linker strips the page's
    // constructor and rendering any unmatched route crashes the whole app with
    // "CtorNotLocated, PoWatch.Client.Layout.NotFound". Rooting the type here keeps it instantiable.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Layout.NotFound))]
    public App()
    {
    }
}

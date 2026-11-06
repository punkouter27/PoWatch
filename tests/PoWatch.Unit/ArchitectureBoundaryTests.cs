using System.Reflection;
using PoWatch.Application.Features.Sessions;
using PoWatch.Domain.Models;
using PoWatch.Shared.Models;

namespace PoWatch.Unit;

/// <summary>
/// Architecture guard tests. The Vertical Slice / layered boundaries are enforced by
/// convention only, so these fail the moment a project takes a dependency that inverts the intended
/// module boundaries (e.g. Application reaching into Infrastructure, or Shared stopping being DTO-only).
///
/// <see cref="Assembly.GetReferencedAssemblies"/> reflects the <b>compiled</b> reference set, so an
/// unused <c>using</c> never trips these — only a real, used dependency does.
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    private const string Domain = "PoWatch.Domain";
    private const string Application = "PoWatch.Application";
    private const string Infrastructure = "PoWatch.Infrastructure";
    private const string Api = "PoWatch.Api";

    private static string[] ReferencedPoWatchAssemblies(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("PoWatch.", StringComparison.Ordinal))
            .ToArray();

    // PoWatch.Shared is the cross-boundary DTO contract: it must not depend on any other PoWatch project.
    [Fact]
    public void Shared_is_dto_only_and_references_no_other_project()
    {
        foreach (string forbidden in new string[] { Domain, Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(forbidden, ReferencedPoWatchAssemblies(typeof(SessionDto).Assembly));
        }
    }

    // PoWatch.Domain is the innermost layer: no outward dependencies at all.
    [Fact]
    public void Domain_depends_on_no_outer_layer()
    {
        foreach (string forbidden in new string[] { Application, Infrastructure, Api })
        {
            Assert.DoesNotContain(forbidden, ReferencedPoWatchAssemblies(typeof(Session).Assembly));
        }
    }

    // PoWatch.Application owns contracts + business services; concrete infrastructure and the web host
    // depend on it, never the reverse (dependency inversion).
    [Fact]
    public void Application_does_not_reference_infrastructure_or_host()
    {
        foreach (string forbidden in new string[] { Infrastructure, Api })
        {
            Assert.DoesNotContain(forbidden, ReferencedPoWatchAssemblies(typeof(SessionService).Assembly));
        }
    }

    // Infrastructure implements Application contracts but must never reference the API host assembly.
    [Fact]
    public void Infrastructure_does_not_reference_the_api_host() =>
        Assert.DoesNotContain(Api, ReferencedPoWatchAssemblies(typeof(PoWatch.Infrastructure.DependencyInjection).Assembly));
}

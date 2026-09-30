using System.Reflection;
using SupermarketBilling.Application.Auditing;
using SupermarketBilling.Domain.Auditing;

namespace SupermarketBilling.UnitTests.Architecture;

/// <summary>Keeps the dependency direction Domain &lt;- Application &lt;- Infrastructure &lt;- Api.</summary>
public sealed class LayeringTests
{
    private static readonly Assembly Domain = typeof(AuditEvent).Assembly;
    private static readonly Assembly Application = typeof(IAuditTrail).Assembly;

    [Fact]
    public void Domain_depends_on_no_other_layer_or_framework()
    {
        var references = ReferencedNames(Domain);

        Assert.DoesNotContain(references, name => name.StartsWith("SupermarketBilling.", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_does_not_depend_on_infrastructure_or_web()
    {
        var references = ReferencedNames(Application);

        Assert.DoesNotContain("SupermarketBilling.Infrastructure", references);
        Assert.DoesNotContain("SupermarketBilling.Api", references);
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Domain_has_no_floating_point_properties()
    {
        // Financial and quantity values must be decimal. This guards every domain type added in later stages.
        var offenders = Domain.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(property =>
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                return type == typeof(double) || type == typeof(float);
            })
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    private static List<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToList();
}

using System.Reflection;
using Emberward.Tests;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer(SlowFirstCollectionOrderer.TypeName, SlowFirstCollectionOrderer.AssemblyName)]

namespace Emberward.Tests;

/// <summary>
/// Starts the test collections that hold slow tests (<c>[Trait("Category", "Slow")]</c>) first, then the rest, the
/// larger ones first. xUnit runs only as many collections at once as the machine has cores, in this order, so a long
/// class that started last would finish seconds after everything else; started first, it overlaps the short ones.
/// The order changes nothing but when each collection starts.
/// </summary>
public sealed class SlowFirstCollectionOrderer : ITestCollectionOrderer
{
    public const string TypeName = "Emberward.Tests.SlowFirstCollectionOrderer";
    public const string AssemblyName = "Emberward.Tests";

    private const string ClassCollectionPrefix = "Test collection for ";

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var assembly = typeof(SlowFirstCollectionOrderer).Assembly;
        return testCollections
            .Select(collection => (Collection: collection, Weight: Weigh(TestClassesOf(collection, assembly))))
            .OrderByDescending(c => c.Weight.SlowTests)
            .ThenByDescending(c => c.Weight.Tests)
            .ThenBy(c => c.Collection.DisplayName, StringComparer.Ordinal)
            .Select(c => c.Collection)
            .ToList();
    }

    /// <summary>
    /// The classes in a collection: the one class of a default collection, or every class that names a defined
    /// collection in its <see cref="CollectionAttribute"/>.
    /// </summary>
    private static IEnumerable<Type> TestClassesOf(ITestCollection collection, Assembly assembly)
    {
        if (collection.CollectionDefinition is null)
        {
            if (!collection.DisplayName.StartsWith(ClassCollectionPrefix, StringComparison.Ordinal))
                return [];
            string typeName = collection.DisplayName[ClassCollectionPrefix.Length..];
            return assembly.GetType(typeName) is { } type ? [type] : [];
        }

        return assembly.GetTypes().Where(type => type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Count == 1
            && attribute.ConstructorArguments[0].Value is string name
            && name == collection.DisplayName));
    }

    private static (int SlowTests, int Tests) Weigh(IEnumerable<Type> classes)
    {
        int slow = 0, tests = 0;
        foreach (var type in classes)
        {
            // A class marked Slow makes every test in it slow, as the Category filter sees it.
            bool slowClass = type.GetCustomAttributesData().Any(IsSlowTrait);
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.IsDefined(typeof(FactAttribute), inherit: true))
                    continue;
                tests++;
                if (slowClass || method.GetCustomAttributesData().Any(IsSlowTrait))
                    slow++;
            }
        }

        return (slow, tests);
    }

    private static bool IsSlowTrait(CustomAttributeData attribute) =>
        attribute.AttributeType == typeof(TraitAttribute)
        && attribute.ConstructorArguments is [{ Value: "Category" }, { Value: "Slow" }];
}

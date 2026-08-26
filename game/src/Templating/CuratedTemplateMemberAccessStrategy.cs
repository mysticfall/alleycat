using System.Linq.Expressions;
using System.Reflection;
using Fluid;

namespace AlleyCat.Templating;

/// <summary>
/// Curated Fluid member-access policy for the game assembly: interface properties marked with
/// <see cref="TemplateExposedAttribute" /> are registered for their declaring interfaces, interfaces marked with
/// <see cref="TemplateSealedAttribute" /> hide every unlisted member, and all other types keep the engine's
/// permissive reflective member access.
/// </summary>
/// <remarks>
/// <para>
/// The strategy owns its <see cref="UnsafeMemberAccessStrategy" /> instance (the shared
/// <see cref="UnsafeMemberAccessStrategy.Instance" /> singleton is never mutated), so curated registrations and the
/// permissive fallback coexist: curated names resolve through their accessors first, sealed surfaces yield nil for
/// unlisted names, and unknown plain objects fall back to reflecting their public members exactly as before.
/// </para>
/// <para>
/// Fluid resolves members by probing the runtime type, then its base-class chain, then all implemented interfaces
/// in an unspecified order, checking each interface's name-specific entry before its wildcard entry. A naive
/// null-valued wildcard on a sealed interface would therefore shadow curated members on sibling interfaces, so the
/// sealing wildcard is name-aware: it dispatches globally curated names through their compiled getters and yields
/// nil for everything else, making resolution independent of interface enumeration order.
/// </para>
/// </remarks>
internal sealed class CuratedTemplateMemberAccessStrategy : MemberAccessStrategy
{
    /// <summary>Fluid's conventional wildcard key matching any member name registered for a type.</summary>
    private const string WildcardMemberName = "*";

    private static readonly Lazy<CuratedTemplateMemberAccessStrategy> _sharedStrategy =
        new(CreateFromAssemblyDiscovery);

    private readonly UnsafeMemberAccessStrategy _permissive = new();
    private readonly SealedInterfaceAccessor _sealedAccessor;
    private readonly Lock _registrationLock = new();
    private volatile Dictionary<string, CuratedPropertyAccessor> _curatedMembers = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an empty curated strategy. Curated members and sealed interfaces must be registered through
    /// <see cref="RegisterCuratedMember" /> and <see cref="SealInterface" />.
    /// </summary>
    public CuratedTemplateMemberAccessStrategy()
    {
        _sealedAccessor = new SealedInterfaceAccessor(this);
    }

    /// <summary>
    /// Gets the process-wide strategy populated by one-time reflection discovery over the game assembly's
    /// annotated interfaces. The engine may be instantiated repeatedly, so discovery runs lazily exactly once and
    /// every engine shares the resulting registrations.
    /// </summary>
    public static CuratedTemplateMemberAccessStrategy Shared => _sharedStrategy.Value;

    /// <inheritdoc />
    public override IMemberAccessor GetAccessor(Type type, string name) =>
        // Fluid declares a non-nullable result although its own strategies return null for unresolved members (the
        // caller then yields nil); the curated lookup mirrors that contract.
        _permissive.GetAccessor(type, name)!;

    /// <inheritdoc />
    public override void Register(Type type, IEnumerable<KeyValuePair<string, IMemberAccessor>> accessors)
        => _permissive.Register(type, accessors);

    /// <summary>
    /// Registers an interface property as a curated template member. Registration is idempotent per
    /// (interface, member) pair.
    /// </summary>
    /// <param name="property">Interface property marked with <see cref="TemplateExposedAttribute" />.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the member name is already curated on a different interface (the single-level rule).
    /// </exception>
    public void RegisterCuratedMember(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);

        Type declaringInterface = ValidateCuratedProperty(property);

        lock (_registrationLock)
        {
            if (_curatedMembers.TryGetValue(property.Name, out CuratedPropertyAccessor? existing) &&
                existing.DeclaringInterface != declaringInterface)
            {
                throw new InvalidOperationException(
                    $"Template member '{property.Name}' is curated on both " +
                    $"'{existing.DeclaringInterface.FullName}' and '{declaringInterface.FullName}'. Curate the " +
                    "member on exactly one interface: Fluid enumerates a type's interfaces in an unspecified " +
                    "order, so duplicate curated names would resolve ambiguously.");
            }

            CuratedPropertyAccessor accessor = new(property);

            // Copy-on-write keeps the sealing wildcard's lock-free reads safe while registration is still running.
            Dictionary<string, CuratedPropertyAccessor> updated = new(_curatedMembers, StringComparer.Ordinal)
            {
                [property.Name] = accessor,
            };
            _curatedMembers = updated;

            _permissive.Register(
                declaringInterface,
                new Dictionary<string, IMemberAccessor>
                {
                    [property.Name] = accessor,
                });
        }
    }

    /// <summary>
    /// Seals an interface's template surface so its implementers expose exactly the curated members and render
    /// every other member access as nil. Sealing is idempotent per interface.
    /// </summary>
    /// <param name="interfaceType">Interface marked with <see cref="TemplateSealedAttribute" />.</param>
    public void SealInterface(Type interfaceType)
    {
        ArgumentNullException.ThrowIfNull(interfaceType);

        if (!interfaceType.IsInterface)
        {
            throw new ArgumentException(
                $"Only interfaces can seal their template surface, but '{interfaceType.FullName}' is not an " +
                "interface.",
                nameof(interfaceType));
        }

        _permissive.Register(
            interfaceType,
            new Dictionary<string, IMemberAccessor>
            {
                [WildcardMemberName] = _sealedAccessor,
            });
    }

    private static CuratedTemplateMemberAccessStrategy CreateFromAssemblyDiscovery()
    {
        CuratedTemplateMemberAccessStrategy strategy = new();

        foreach (Type type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (!type.IsInterface)
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties())
            {
                if (property.GetCustomAttribute<TemplateExposedAttribute>() is not null)
                {
                    strategy.RegisterCuratedMember(property);
                }
            }

            if (type.GetCustomAttribute<TemplateSealedAttribute>() is not null)
            {
                strategy.SealInterface(type);
            }
        }

        return strategy;
    }

    private static Type ValidateCuratedProperty(PropertyInfo property)
    {
        return property.DeclaringType is not { IsInterface: true } declaringInterface
            ? throw new ArgumentException(
                $"Template member '{property.Name}' must be declared on an interface, but it is declared on " +
                $"'{property.DeclaringType?.FullName ?? "<unknown>"}'.",
                nameof(property))
            : property.GetGetMethod() is null
                ? throw new ArgumentException(
                    $"Template member '{property.Name}' must expose a getter for template rendering.",
                    nameof(property))
                : property.GetIndexParameters().Length > 0
                    ? throw new ArgumentException(
                        $"Template member '{property.Name}' must not be an indexer; templates address members by " +
                        "name.",
                        nameof(property))
                    : declaringInterface;
    }

    /// <summary>
    /// Reads a curated interface property through a compiled getter. The getter casts the runtime object to the
    /// declaring interface before the property access, which keeps default interface method dispatch correct.
    /// </summary>
    private sealed class CuratedPropertyAccessor(PropertyInfo property) : IMemberAccessor
    {
        private readonly Func<object, object?> _getter = CompilePropertyGetter(property);

        public Type DeclaringInterface { get; } = property.DeclaringType!;

        public object? Get(object obj, string name, TemplateContext context) => _getter(obj);

        public object? Read(object obj) => _getter(obj);

        private static Func<object, object?> CompilePropertyGetter(PropertyInfo property)
        {
            Type declaringInterface = property.DeclaringType!;
            ParameterExpression instance = Expression.Parameter(typeof(object), "obj");
            Expression body = Expression.Convert(
                Expression.Property(Expression.Convert(instance, declaringInterface), property),
                typeof(object));
            return Expression.Lambda<Func<object, object?>>(body, instance).Compile();
        }
    }

    /// <summary>
    /// Wildcard accessor registered on sealed interfaces. Fluid may probe a sealed interface before the interface
    /// declaring a curated member, so this accessor dispatches globally curated names itself and yields null (which
    /// renders as nil) for every unlisted name, keeping sealed surfaces order-independent.
    /// </summary>
    private sealed class SealedInterfaceAccessor(CuratedTemplateMemberAccessStrategy owner) : IMemberAccessor
    {
        public object? Get(object obj, string name, TemplateContext context)
        {
            return owner._curatedMembers.TryGetValue(name, out CuratedPropertyAccessor? member) &&
                member.DeclaringInterface.IsInstanceOfType(obj)
                ? member.Read(obj)
                : null;
        }
    }
}

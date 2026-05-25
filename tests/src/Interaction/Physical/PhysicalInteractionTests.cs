using System.Reflection;
using System.Reflection.Emit;
using AlleyCat.Body;
using AlleyCat.Common;
using AlleyCat.IK;
using AlleyCat.Interaction.Physical;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Interaction.Physical;

/// <summary>
/// Unit coverage for BODY-008 physical interaction API contracts.
/// </summary>
public sealed class PhysicalInteractionTests
{
    /// <summary>
    /// Verifies the polymorphic interaction API exposes a common interface and an impact subtype.
    /// </summary>
    [Fact]
    public void PhysicalInteractionApi_DefinesPolymorphicImpactSubtypeWithoutKindEnum()
    {
        Assert.True(typeof(IPhysicalInteraction).IsInterface);
        Assert.True(typeof(IImpactPhysicalInteraction).IsInterface);
        Assert.True(typeof(IPhysicalInteraction).IsAssignableFrom(typeof(IImpactPhysicalInteraction)));
        Assert.True(typeof(IImpactPhysicalInteraction).IsAssignableFrom(typeof(ImpactPhysicalInteraction)));
        Assert.Null(typeof(IPhysicalInteraction).Assembly.GetType("AlleyCat.Interaction.Physical.PhysicalInteractionKind"));
        Assert.Null(typeof(IPhysicalInteraction).Assembly.GetType("AlleyCat.Interaction.Physical.PhysicalInteraction"));
        Assert.Null(typeof(IPhysicalInteraction).GetProperty("Kind"));
    }

    /// <summary>
    /// Verifies impact interactions preserve universal contact data and impact-specific velocity.
    /// </summary>
    [Fact]
    public void ImpactPhysicalInteraction_PreservesSourceEventContactPointAndVelocity()
    {
        TestImpactPhysicalInteractionSource source = new(new Vector3(4.0f, 5.0f, 6.0f));
        Vector3 contactPoint = new(1.0f, 2.0f, 3.0f);
        ImpactPhysicalInteraction interaction = new(source, contactPoint, source.Velocity);
        IPhysicalInteraction commonInteraction = interaction;
        IImpactPhysicalInteraction impactInteraction = interaction;

        Assert.Same(source, interaction.Source);
        Assert.Same(source, commonInteraction.Source);
        Assert.Same(source, impactInteraction.Source);
        Assert.Equal(new Vector3(1.0f, 2.0f, 3.0f), commonInteraction.ContactPoint);
        Assert.Equal(new Vector3(4.0f, 5.0f, 6.0f), impactInteraction.Velocity);
    }

    /// <summary>
    /// Verifies sources are tagged data providers and do not own receiver polling.
    /// </summary>
    [Fact]
    public void IPhysicalInteractionSource_IsTaggedMarkerWithoutReceiverQuery()
    {
        Type sourceType = typeof(IPhysicalInteractionSource);

        Assert.True(typeof(ITagged).IsAssignableFrom(sourceType));
        Assert.Null(sourceType.GetMethod("QueryCompatibleReceivers"));
        Assert.Null(sourceType.GetMethod("CreateInteractionFor"));
        Assert.Equal([typeof(ITagged)], sourceType.GetInterfaces());
    }

    /// <summary>
    /// Verifies receivers create, relay, and return impact interactions from impact sources.
    /// </summary>
    [Fact]
    public void IPhysicalInteractionReceiver_InteractsWithImpactSourceByCreatingInteraction()
    {
        TestImpactPhysicalInteractionSource source = new(Vector3.Forward);
        TestImpactReceiver receiver = new(Vector3.Up, "Head", "ImpactTarget");

        IPhysicalInteraction? producedInteraction = receiver.InteractWith(source);

        IImpactPhysicalInteraction receivedInteraction = Assert.IsAssignableFrom<IImpactPhysicalInteraction>(
            Assert.Single(receiver.ReceivedInteractions));
        Assert.Same(producedInteraction, receivedInteraction);
        Assert.Same(source, receivedInteraction.Source);
        Assert.Equal(Vector3.Up, receivedInteraction.ContactPoint);
        Assert.Equal(Vector3.Forward, receivedInteraction.Velocity);
        Assert.Equal(["HandSource"], [.. source.Tags]);
        Assert.Equal(["Head", "ImpactTarget"], [.. receiver.Tags]);
    }

    /// <summary>
    /// Verifies unsupported source types preserve the nullable no-op path.
    /// </summary>
    [Fact]
    public void IPhysicalInteractionReceiver_ReturnsNullWhenSourceTypeIsUnsupported()
    {
        TestPhysicalInteractionSource source = new();
        TestImpactReceiver receiver = new(Vector3.Up, "Head", "ImpactTarget");

        IPhysicalInteraction? producedInteraction = receiver.InteractWith(source);

        Assert.Null(producedInteraction);
        Assert.Empty(receiver.ReceivedInteractions);
    }

    /// <summary>
    /// Verifies the receipt wrapper is shaped for Godot-compatible preservation of the domain interaction value.
    /// </summary>
    [Fact]
    public void PhysicalInteractionReceipt_WrapsInteractionForGodotSignalInterop()
    {
        ConstructorInfo constructor = typeof(PhysicalInteractionReceipt).GetConstructors().Single();

        Assert.True(typeof(RefCounted).IsAssignableFrom(typeof(PhysicalInteractionReceipt)));
        Assert.Equal([typeof(IPhysicalInteraction)], [.. constructor.GetParameters().Select(parameter => parameter.ParameterType)]);
        Assert.Equal(typeof(IPhysicalInteraction), typeof(PhysicalInteractionReceipt).GetProperty(nameof(PhysicalInteractionReceipt.Interaction))?.PropertyType);
    }

    /// <summary>
    /// Verifies receiver metadata avoids over-exposing generated bone ownership fields in the inspector.
    /// </summary>
    [Fact]
    public void PhysicalBodyPart3D_ExportsOnlyReceiverOwnedConfigurableMetadata()
    {
        Type type = typeof(PhysicalBodyPart3D);
        Type receiverType = typeof(IPhysicalInteractionReceiver);

        Assert.DoesNotContain(Attribute.GetCustomAttributes(type.GetProperty(nameof(PhysicalBodyPart3D.BoneName))!), attribute => attribute is ExportAttribute);
        Assert.DoesNotContain(Attribute.GetCustomAttributes(type.GetProperty(nameof(PhysicalBodyPart3D.BoneIndex))!), attribute => attribute is ExportAttribute);
        Assert.Null(Attribute.GetCustomAttribute(type.GetProperty(nameof(PhysicalBodyPart3D.Tags))!, typeof(ExportAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(type.GetProperty(nameof(PhysicalBodyPart3D.AuthoredTags))!, typeof(ExportAttribute)));
        Assert.True(typeof(ITagged).IsAssignableFrom(receiverType));
        Assert.True(typeof(ITagged).IsAssignableFrom(typeof(IPhysicalInteractionSource)));
        Assert.Equal(typeof(IReadOnlySet<string>), receiverType.GetInterfaces()
            .Single(interfaceType => interfaceType == typeof(ITagged))
            .GetProperty(nameof(ITagged.Tags))?.PropertyType);
        Assert.Equal(typeof(IReadOnlySet<string>), typeof(ITagged).GetProperty(nameof(ITagged.Tags))?.PropertyType);
        Assert.Equal(typeof(IReadOnlySet<string>), type.GetProperty(nameof(PhysicalBodyPart3D.Tags))?.PropertyType);
        Assert.False(typeof(ISet<string>).IsAssignableFrom(type.GetProperty(nameof(PhysicalBodyPart3D.Tags))?.PropertyType));
        Assert.NotNull(receiverType.GetMethod(nameof(IPhysicalInteractionReceiver.InteractWith)));
        Assert.Null(receiverType.GetMethod("ReceiveInteractionFrom"));
        Assert.Null(typeof(IPhysicalInteractionSource).GetMethod("QueryCompatibleReceivers"));
        Assert.Null(type.GetProperty("BodyRegion"));
        Assert.Null(type.GetProperty("SkeletonPath"));
        Assert.Null(type.GetProperty("LastInteraction"));
        Assert.Null(type.GetProperty("ReceiveCount"));
    }

    /// <summary>
    /// Verifies body-part interaction receipt exposes a Godot-compatible signal surface.
    /// </summary>
    [Fact]
    public void PhysicalBodyPart3D_DefinesInteractionReceivedSignalContract()
    {
        Type signalDelegateType = typeof(PhysicalBodyPart3D).GetNestedType(
            "PhysicalInteractionReceivedEventHandler",
            BindingFlags.Public)
            ?? throw new Xunit.Sdk.XunitException("Expected PhysicalBodyPart3D to define an interaction signal delegate.");
        MethodInfo invoke = signalDelegateType.GetMethod("Invoke")
            ?? throw new Xunit.Sdk.XunitException("Expected interaction signal delegate to expose an Invoke method.");
        Type[] parameterTypes = [.. invoke.GetParameters().Select(parameter => parameter.ParameterType)];

        Assert.NotNull(Attribute.GetCustomAttribute(signalDelegateType, typeof(SignalAttribute)));
        Assert.Equal([typeof(PhysicalInteractionReceipt), typeof(int), typeof(string[])], parameterTypes);
    }

    /// <summary>
    /// Verifies body-part interaction receipt emits through Godot without retaining mutable receive history.
    /// </summary>
    [Fact]
    public void PhysicalBodyPart3D_InteractWith_EmitsSignalWithoutReceiveHistory()
    {
        MethodInfo interactWith = typeof(PhysicalBodyPart3D).GetMethod(
            nameof(PhysicalBodyPart3D.InteractWith))
            ?? throw new Xunit.Sdk.XunitException("Expected PhysicalBodyPart3D.InteractWith to exist.");
        MethodInfo emitSignal = typeof(GodotObject).GetMethods()
            .Single(method =>
                method.Name == nameof(GodotObject.EmitSignal)
                && method.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }]
                && first == typeof(StringName)
                && second == typeof(Variant[]));
        ConstructorInfo receiptConstructor = typeof(PhysicalInteractionReceipt).GetConstructors().Single();

        Assert.Equal(1, CountCallsToMethod(interactWith, emitSignal));
        Assert.Equal(1, CountCallsToMethod(interactWith, receiptConstructor));
        Assert.Null(typeof(PhysicalBodyPart3D).GetMethod("RequestInteractionFrom"));
        Assert.Null(typeof(PhysicalBodyPart3D).GetMethod("ReceiveInteractionFrom"));
        Assert.Null(typeof(IPhysicalInteractionSource).GetMethod("CreateInteractionFor"));
        Assert.Null(typeof(IPhysicalInteractionSource).GetMethod("QueryCompatibleReceivers"));
        Assert.Null(typeof(PhysicalBodyPart3D).GetProperty("LastInteraction"));
        Assert.Null(typeof(PhysicalBodyPart3D).GetProperty("ReceiveCount"));
    }

    /// <summary>
    /// Verifies the pluggable impact source component exposes only source-owned data and metadata.
    /// </summary>
    [Fact]
    public void HandDynamicBodyInteractionController_ImplementsImpactSourceContract()
    {
        Type type = typeof(HandDynamicBodyInteractionController);

        Assert.True(typeof(IImpactPhysicalInteractionSource).IsAssignableFrom(type));
        Assert.True(typeof(IPhysicalInteractionSource).IsAssignableFrom(type));
        Assert.Null(typeof(IImpactPhysicalInteractionSource).GetProperty("ContactPoint"));
        Assert.Equal(typeof(IReadOnlySet<string>), type.GetProperty(nameof(HandDynamicBodyInteractionController.Tags))?.PropertyType);
        Assert.False(typeof(ISet<string>).IsAssignableFrom(type.GetProperty(nameof(HandDynamicBodyInteractionController.Tags))?.PropertyType));
        Assert.Equal(typeof(Vector3), type.GetProperty(nameof(HandDynamicBodyInteractionController.Velocity))?.PropertyType);
        Assert.Null(type.GetMethod("SetContactPoint", [typeof(Vector3)]));
        Assert.Null(type.GetMethod("SetImpact", [typeof(Vector3), typeof(Vector3)]));
        Assert.Null(type.GetMethod("QueryCompatibleReceivers"));
        Assert.Null(type.GetMethod("CreateInteractionFor"));
    }

    /// <summary>
    /// Verifies the rigid body receiver component exposes a Godot-authored impact-to-impulse contract.
    /// </summary>
    [Fact]
    public void RigidBodyImpactInteractionReceiver3D_ImplementsReceiverImpulseContract()
    {
        Type type = typeof(RigidBodyImpactInteractionReceiver3D);
        Type signalDelegateType = type.GetNestedType("PhysicalInteractionReceivedEventHandler", BindingFlags.Public)
            ?? throw new Xunit.Sdk.XunitException("Expected rigid body receiver to define an interaction signal delegate.");
        MethodInfo invoke = signalDelegateType.GetMethod("Invoke")
            ?? throw new Xunit.Sdk.XunitException("Expected interaction signal delegate to expose an Invoke method.");
        Type[] parameterTypes = [.. invoke.GetParameters().Select(parameter => parameter.ParameterType)];

        Assert.True(typeof(Node).IsAssignableFrom(type));
        Assert.True(typeof(IPhysicalInteractionReceiver).IsAssignableFrom(type));
        Assert.NotNull(Attribute.GetCustomAttribute(type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.Body))!, typeof(ExportAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.ImpulseScale))!, typeof(ExportAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.MinimumSpeedMetresPerSecond))!, typeof(ExportAttribute)));
        Assert.NotNull(Attribute.GetCustomAttribute(type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.AuthoredTags))!, typeof(ExportAttribute)));
        Assert.Null(Attribute.GetCustomAttribute(type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.Tags))!, typeof(ExportAttribute)));
        Assert.Equal(typeof(IReadOnlySet<string>), type.GetProperty(nameof(RigidBodyImpactInteractionReceiver3D.Tags))?.PropertyType);
        Assert.NotNull(type.GetMethod(nameof(RigidBodyImpactInteractionReceiver3D.InteractWith), [typeof(IPhysicalInteractionSource)]));
        Assert.NotNull(type.GetMethod(nameof(RigidBodyImpactInteractionReceiver3D.InteractWith), [typeof(IPhysicalInteractionSource), typeof(Vector3)]));
        Assert.NotNull(Attribute.GetCustomAttribute(signalDelegateType, typeof(SignalAttribute)));
        Assert.Equal([typeof(PhysicalInteractionReceipt), typeof(string[])], parameterTypes);
    }

    /// <summary>
    /// Verifies BODY-008 does not retain the rejected Area3D collision relay type.
    /// </summary>
    [Fact]
    public void PhysicalInteractionApi_DoesNotRequireAreaCollisionRelay()
    {
        Assert.Null(typeof(IPhysicalInteraction).Assembly.GetType("AlleyCat.Interaction.Physical.PhysicalInteractionCollisionRelay3D"));
        Assert.Null(typeof(IPhysicalInteraction).Assembly.GetType("AlleyCat.Interaction.Physical.PhysicalInteractionImpactSource3D"));
        Assert.Null(typeof(IPhysicalInteractionSource).GetMethod("QueryCompatibleReceivers"));
        Assert.Null(typeof(IPhysicalInteractionSource).GetMethod("CreateInteractionFor"));
    }

    private static int CountCallsToMethod(MethodInfo method, MethodBase calledMethod)
    {
        byte[] il = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new Xunit.Sdk.XunitException($"Expected {method.Name} to have an IL body.");
        int count = 0;
        int offset = 0;

        while (offset < il.Length)
        {
            OpCode opCode = ReadOpCode(il, ref offset);
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                int metadataToken = BitConverter.ToInt32(il, offset);
                MethodBase? resolvedMethod = method.Module.ResolveMethod(
                    metadataToken,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments());
                if (resolvedMethod == calledMethod)
                {
                    count += 1;
                }
            }

            offset += GetOperandSize(opCode.OperandType, il, offset);
        }

        return count;
    }

    private static OpCode ReadOpCode(byte[] il, ref int offset)
    {
        byte value = il[offset++];
        return value == 0xFE ? _multiByteOpCodes[il[offset++]] : _singleByteOpCodes[value];
    }

    private static int GetOperandSize(OperandType operandType, byte[] il, int offset)
    {
        return _fixedOperandSizes.TryGetValue(operandType, out int fixedSize)
            ? fixedSize
            : operandType == OperandType.InlineSwitch
                ? 4 + (BitConverter.ToInt32(il, offset) * 4)
                : throw new NotSupportedException($"Unsupported IL operand type {operandType}.");
    }

    private static readonly IReadOnlyDictionary<OperandType, int> _fixedOperandSizes = new Dictionary<OperandType, int>
    {
        [OperandType.InlineNone] = 0,
        [OperandType.ShortInlineBrTarget] = 1,
        [OperandType.ShortInlineI] = 1,
        [OperandType.ShortInlineVar] = 1,
        [OperandType.InlineVar] = 2,
        [OperandType.InlineBrTarget] = 4,
        [OperandType.InlineField] = 4,
        [OperandType.InlineI] = 4,
        [OperandType.InlineMethod] = 4,
        [OperandType.InlineSig] = 4,
        [OperandType.InlineString] = 4,
        [OperandType.InlineTok] = 4,
        [OperandType.InlineType] = 4,
        [OperandType.ShortInlineR] = 4,
        [OperandType.InlineI8] = 8,
        [OperandType.InlineR] = 8,
    };

    private static readonly OpCode[] _singleByteOpCodes = BuildOpCodeTable(false);

    private static readonly OpCode[] _multiByteOpCodes = BuildOpCodeTable(true);

    private static OpCode[] BuildOpCodeTable(bool multiByte)
    {
        var opCodes = new OpCode[256];
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            ushort value = unchecked((ushort)opCode.Value);
            bool isMultiByte = (value & 0xFF00) == 0xFE00;
            if (isMultiByte == multiByte)
            {
                opCodes[value & 0xFF] = opCode;
            }
        }

        return opCodes;
    }

    private sealed class TestPhysicalInteractionSource : IPhysicalInteractionSource
    {
        public IReadOnlySet<string> Tags { get; } = new SortedSet<string>(["GenericSource"], StringComparer.Ordinal);

    }

    private sealed class TestImpactPhysicalInteractionSource(Vector3 velocity) : IImpactPhysicalInteractionSource
    {
        public IReadOnlySet<string> Tags { get; } = new SortedSet<string>(["HandSource"], StringComparer.Ordinal);

        public Vector3 Velocity => velocity;

    }

    private sealed class TestImpactReceiver(Vector3 contactPoint, params string[] tags) : IPhysicalInteractionReceiver
    {
        public IReadOnlySet<string> Tags { get; } = new SortedSet<string>(tags, StringComparer.Ordinal);

        public List<IPhysicalInteraction> ReceivedInteractions { get; } = [];

        public IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source is not IImpactPhysicalInteractionSource impactSource)
            {
                return null;
            }

            ImpactPhysicalInteraction interaction = new(impactSource, contactPoint, impactSource.Velocity);
            ReceivedInteractions.Add(interaction);
            return interaction;
        }
    }

    private sealed class TestUnsupportedReceiver(params string[] tags) : IPhysicalInteractionReceiver
    {
        public IReadOnlySet<string> Tags { get; } = new SortedSet<string>(tags, StringComparer.Ordinal);

        public IPhysicalInteraction? InteractWith(IPhysicalInteractionSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return null;
        }
    }
}

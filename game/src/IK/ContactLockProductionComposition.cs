using AlleyCat.Control.Locomotion;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

#pragma warning disable CS1591

/// <summary>Role-neutral installation primitive for the reusable production contact-lock boundary.</summary>
public static class ContactLockProductionComposition
{
    public const string DefaultCataloguePath =
        "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json";

    public static ContactLockCoordinator LoadCoordinator() => LoadCoordinator(DefaultCataloguePath);

    public static ContactLockCoordinator LoadCoordinator(string cataloguePath, string? contactEvidencePath = null, float? maximumTranslation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePath);
        string catalogueJson = Godot.FileAccess.GetFileAsString(cataloguePath);
        if (string.IsNullOrWhiteSpace(catalogueJson))
        {
            throw new InvalidOperationException($"Contact-lock composition requires catalogue '{cataloguePath}'.");
        }
        var catalogue = StandingLocomotionCatalogue.Parse(catalogueJson, cataloguePath);
        return LoadCoordinator(catalogue, cataloguePath, contactEvidencePath, maximumTranslation);
    }

    public static ContactLockCoordinator LoadCoordinator(
        StandingLocomotionCatalogue catalogue,
        string cataloguePath,
        string? contactEvidencePath = null,
        float? maximumTranslation = null)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentException.ThrowIfNullOrWhiteSpace(cataloguePath);
        StandingLocomotionContactEvidence declaration = catalogue.ContactEvidence
            ?? throw new InvalidOperationException("Contact-lock composition requires a catalogue contact-evidence declaration.");
        string trackPath = ResolveDeclaredPath(cataloguePath, contactEvidencePath ?? declaration.Path);
        string json = Godot.FileAccess.GetFileAsString(trackPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"Contact-lock composition requires declared track '{trackPath}'.");
        }
        var coordinator = new ContactLockCoordinator(AuthoredPerFootContactTrackV1.Load(json, catalogue));
        if (maximumTranslation.HasValue)
        {
            if (!float.IsFinite(maximumTranslation.Value) || maximumTranslation.Value <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTranslation));
            }
            coordinator.MaximumTranslation = maximumTranslation.Value;
        }
        return coordinator;
    }

    internal static string ResolveDeclaredPath(string cataloguePath, string declaredPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declaredPath);
        if (declaredPath.StartsWith("res://", StringComparison.Ordinal)
            || declaredPath.StartsWith("user://", StringComparison.Ordinal))
        {
            return declaredPath;
        }
        int separator = cataloguePath.LastIndexOf('/');
        return separator < 0 || declaredPath.Contains("..", StringComparison.Ordinal)
            ? throw new InvalidDataException("Contact evidence path must be absolute or safely relative to the catalogue.")
            : $"{cataloguePath[..separator]}/{declaredPath}";
    }

    public static ContactLockProductionBinding Install(
        Node3D actor,
        CharacterIK characterIK,
        ContactLockCoordinator coordinator,
        IKTargetIntentProvider? leftFallback = null,
        IKTargetIntentProvider? rightFallback = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(characterIK);
        ArgumentNullException.ThrowIfNull(coordinator);
        Node3D leftTarget = characterIK.LeftFootIKTarget
            ?? throw new InvalidOperationException("Contact-lock composition requires the left animation-synchronised foot target.");
        Node3D rightTarget = characterIK.RightFootIKTarget
            ?? throw new InvalidOperationException("Contact-lock composition requires the right animation-synchronised foot target.");

        IKTargetIntentProvider? previousLeftProvider = characterIK.LeftFootTargetIntentProvider;
        IKTargetIntentProvider? previousRightProvider = characterIK.RightFootTargetIntentProvider;
        IKTargetIntentProvider? previousLeftFallback = characterIK.LeftFootFallbackIntentProvider;
        IKTargetIntentProvider? previousRightFallback = characterIK.RightFootFallbackIntentProvider;
        bool ownsLeftFallback = leftFallback is null;
        bool ownsRightFallback = rightFallback is null;
        IKTargetIntentProvider? installedLeftFallback = leftFallback;
        IKTargetIntentProvider? installedRightFallback = rightFallback;
        ContactLockFootTargetProvider? left = null;
        ContactLockFootTargetProvider? right = null;
        try
        {
            installedLeftFallback ??= AddFallback(characterIK, leftTarget, "LeftAnimationFootFallback");
            installedRightFallback ??= AddFallback(characterIK, rightTarget, "RightAnimationFootFallback");
            left = AddProvider(
                characterIK, actor, coordinator, LimbSide.Left, leftTarget, installedLeftFallback, "LeftContactLockProvider");
            right = AddProvider(
                characterIK, actor, coordinator, LimbSide.Right, rightTarget, installedRightFallback, "RightContactLockProvider");

            characterIK.LeftFootTargetIntentProvider = left;
            characterIK.RightFootTargetIntentProvider = right;
            characterIK.LeftFootFallbackIntentProvider = installedLeftFallback;
            characterIK.RightFootFallbackIntentProvider = installedRightFallback;
            return new(
                characterIK,
                coordinator,
                left,
                right,
                installedLeftFallback,
                installedRightFallback,
                previousLeftProvider,
                previousRightProvider,
                previousLeftFallback,
                previousRightFallback,
                ownsLeftFallback,
                ownsRightFallback);
        }
        catch
        {
            characterIK.LeftFootTargetIntentProvider = previousLeftProvider;
            characterIK.RightFootTargetIntentProvider = previousRightProvider;
            characterIK.LeftFootFallbackIntentProvider = previousLeftFallback;
            characterIK.RightFootFallbackIntentProvider = previousRightFallback;
            QueueFree(left);
            QueueFree(right);
            if (ownsLeftFallback)
            {
                QueueFree(installedLeftFallback);
            }
            if (ownsRightFallback)
            {
                QueueFree(installedRightFallback);
            }
            coordinator.Teardown();
            throw;
        }
    }

    private static ContactLockFootTargetProvider AddProvider(
        CharacterIK characterIK,
        Node3D actor,
        ContactLockCoordinator coordinator,
        LimbSide side,
        Node3D source,
        IKTargetIntentProvider fallback,
        string name)
    {
        ContactLockFootTargetProvider provider = new()
        {
            Name = name,
            Side = side,
            AnimationSynchronizedFootTarget = source,
            RootMotionActor = actor,
            Coordinator = coordinator,
            OrdinaryFallbackIntentProvider = fallback,
        };
        characterIK.AddChild(provider);
        return provider;
    }

    private static AnimationSynchronizedFootTargetProvider AddFallback(
        CharacterIK characterIK,
        Node3D source,
        string name)
    {
        AnimationSynchronizedFootTargetProvider provider = new()
        {
            Name = name,
            FootTarget = source
        };
        characterIK.AddChild(provider);
        return provider;
    }

    private static void QueueFree(Node? node)
    {
        if (node is not null && GodotObject.IsInstanceValid(node))
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
    }
}

/// <summary>Installed provider pair with idempotent restoration and teardown semantics.</summary>
public sealed class ContactLockProductionBinding
{
    private readonly CharacterIK _characterIK;
    private IKTargetIntentProvider? _previousLeftProvider;
    private IKTargetIntentProvider? _previousRightProvider;
    private IKTargetIntentProvider? _previousLeftFallback;
    private IKTargetIntentProvider? _previousRightFallback;
    private bool _ownsLeftFallback;
    private bool _ownsRightFallback;
    private bool _tornDown;

    internal ContactLockProductionBinding(
        CharacterIK characterIK,
        ContactLockCoordinator coordinator,
        ContactLockFootTargetProvider leftProvider,
        ContactLockFootTargetProvider rightProvider,
        IKTargetIntentProvider leftFallback,
        IKTargetIntentProvider rightFallback,
        IKTargetIntentProvider? previousLeftProvider,
        IKTargetIntentProvider? previousRightProvider,
        IKTargetIntentProvider? previousLeftFallback,
        IKTargetIntentProvider? previousRightFallback,
        bool ownsLeftFallback,
        bool ownsRightFallback)
    {
        _characterIK = characterIK;
        Coordinator = coordinator;
        LeftProvider = leftProvider;
        RightProvider = rightProvider;
        LeftFallback = leftFallback;
        RightFallback = rightFallback;
        _previousLeftProvider = previousLeftProvider;
        _previousRightProvider = previousRightProvider;
        _previousLeftFallback = previousLeftFallback;
        _previousRightFallback = previousRightFallback;
        _ownsLeftFallback = ownsLeftFallback;
        _ownsRightFallback = ownsRightFallback;
    }

    public ContactLockCoordinator Coordinator
    {
        get;
    }
    public ContactLockFootTargetProvider LeftProvider
    {
        get;
    }
    public ContactLockFootTargetProvider RightProvider
    {
        get;
    }
    public IKTargetIntentProvider LeftFallback
    {
        get;
    }
    public IKTargetIntentProvider RightFallback
    {
        get;
    }


    internal void TransferRestorationStateTo(ContactLockProductionBinding replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        replacement._previousLeftProvider = _previousLeftProvider;
        replacement._previousRightProvider = _previousRightProvider;
        replacement._previousLeftFallback = _previousLeftFallback;
        replacement._previousRightFallback = _previousRightFallback;
        replacement._ownsLeftFallback = _ownsLeftFallback;
        replacement._ownsRightFallback = _ownsRightFallback;
        _ownsLeftFallback = false;
        _ownsRightFallback = false;
    }

    public void Teardown()
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        Coordinator.Teardown();
        if (GodotObject.IsInstanceValid(_characterIK))
        {
            if (ReferenceEquals(_characterIK.LeftFootTargetIntentProvider, LeftProvider))
            {
                _characterIK.LeftFootTargetIntentProvider = _previousLeftProvider;
            }
            if (ReferenceEquals(_characterIK.RightFootTargetIntentProvider, RightProvider))
            {
                _characterIK.RightFootTargetIntentProvider = _previousRightProvider;
            }
            if (_ownsLeftFallback && ReferenceEquals(_characterIK.LeftFootFallbackIntentProvider, LeftFallback))
            {
                _characterIK.LeftFootFallbackIntentProvider = _previousLeftFallback;
            }
            if (_ownsRightFallback && ReferenceEquals(_characterIK.RightFootFallbackIntentProvider, RightFallback))
            {
                _characterIK.RightFootFallbackIntentProvider = _previousRightFallback;
            }
        }

        QueueFree(LeftProvider);
        QueueFree(RightProvider);
        if (_ownsLeftFallback)
        {
            QueueFree(LeftFallback);
        }
        if (_ownsRightFallback)
        {
            QueueFree(RightFallback);
        }
    }

    private static void QueueFree(Node node)
    {
        if (GodotObject.IsInstanceValid(node))
        {
            node.GetParent()?.RemoveChild(node);
            node.QueueFree();
        }
    }
}

using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Logging;
using AlleyCat.Navigation;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.IK;

/// <summary>Read-only publication evidence for the NPC contact-lock composition seam.</summary>
public readonly record struct NPCContactLockPublicationTrace(
    ulong PhysicsTick,
    ulong NavigationGeneration,
    string Role,
    string Clip,
    float Phase,
    long ClockGeneration);

/// <summary>NPC-navigation-scene composition that installs value-only contact adapters after role installers.</summary>
[GlobalClass]
public partial class NPCContactLockPlaytestComposition : Node
{
    private NPCNavigation? _navigation;
    private CharacterIK? _characterIK;
    private ContactLockProductionBinding? _binding;
    private StandingLocomotionCatalogue? _catalogue;

    /// <inheritdoc/>
    [Export]
    public Node? NPC
    {
        get; set;
    }
    /// <inheritdoc/>
    [Export] public bool EnabledForPlaytest { get; set; } = true;
    /// <inheritdoc/>
    public ContactLockCoordinator? Coordinator
    {
        get; private set;
    }
    /// <inheritdoc/>
    public ContactLockFootTargetProvider? LeftProvider
    {
        get; private set;
    }
    /// <inheritdoc/>
    public ContactLockFootTargetProvider? RightProvider
    {
        get; private set;
    }
    /// <inheritdoc/>
    public bool IsInstalled => Coordinator is not null;
    /// <summary>
    /// Whether a terminal ANIM-002 package failure latched installation for this attempt. The unchanged failing
    /// package is not re-read, re-parsed, or re-validated on later physics ticks; a new installation attempt
    /// (scene reload or re-entry) starts fresh.
    /// </summary>
    public bool IsTerminalPackageFailureLatched
    {
        get; private set;
    }
    /// <summary>Terminal package-failure diagnostics reported by this composition.</summary>
    public int TerminalPackageFailureDiagnosticCount
    {
        get; private set;
    }
    /// <summary>Gets the live graph evidence most recently published to the coordinator.</summary>
    public NPCContactLockPublicationTrace LastPublicationTrace
    {
        get; private set;
    }

    /// <inheritdoc/>
    public override void _Ready()
    {
        // AnimationTree evaluates at its default priority after the prior tick's navigation publication. Publish that
        // evaluated state, then resolve the provider pipeline before navigation (-100) consumes its contribution in
        // the same physics tick.
        ProcessPhysicsPriority = -102;
        SetPhysicsProcess(EnabledForPlaytest);
    }

    /// <inheritdoc/>
    public override void _PhysicsProcess(double delta)
    {
        if (!EnabledForPlaytest)
        {
            return;
        }

        if (IsTerminalPackageFailureLatched)
        {
            return;
        }

        if (Coordinator is null && !TryInstallAfterInstallers())
        {
            return;
        }

        NPCNavigation navigation = _navigation!;
        // During the finite-to-idle fade the outgoing finite node remains a live evaluated animation source. Keep its
        // authored release provenance until the fade ends so a bounded non-contact handoff can finish while its
        // ordinary fallback continues to sample the freshly blended skeleton pose.
        bool useFadingSource = string.IsNullOrWhiteSpace(navigation.LastEvaluatedRole)
            && !string.IsNullOrWhiteSpace(navigation.LastFadingFromRole);
        string role = useFadingSource ? navigation.LastFadingFromRole : navigation.LastEvaluatedRole;
        string clip = useFadingSource ? navigation.LastFadingFromClip : navigation.LastEvaluatedClip;
        float phase = useFadingSource
            ? navigation.LastFadingFromNormalisedPhase
            : navigation.LastEvaluatedNormalisedPhase;
        ulong physicsTick = Engine.GetPhysicsFrames();
        bool hasClip = _catalogue!.Clips.TryGetValue(role, out StandingLocomotionClip? evaluatedClip)
            && evaluatedClip.Identity == clip;
        Coordinator!.ObserveLiveState(new ContactLockLiveState(
            hasClip,
            role,
            clip,
            phase,
            hasClip ? _catalogue.PackageVariant : string.Empty,
            hasClip ? evaluatedClip!.Identity : string.Empty,
            hasClip ? _catalogue.ContactEvidence!.Generation : string.Empty,
            physicsTick,
            (float)delta)
        {
            ClockGeneration = navigation.LastSustainedClockGeneration,
            ActorTransform = (NPC as Node3D)?.GlobalTransform ?? Transform3D.Identity,
        });
        LastPublicationTrace = new(
            physicsTick,
            navigation.LastConsumedFrameGeneration,
            role,
            clip,
            phase,
            navigation.LastSustainedClockGeneration);
        _ = _characterIK!.PrepareFootTargetsForMovement(physicsTick, delta);
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        _binding?.Teardown();
        _binding = null;
        _characterIK = null;
        _catalogue = null;
        IsTerminalPackageFailureLatched = false;
        if (_navigation is { } navigation)
        {
            navigation.ContactLockCoordinator = null;
        }
    }

    /// <summary>
    /// Installs once the installer wiring is genuinely complete. Missing nodes remain a legitimate readiness
    /// retry; a missing or invalid ANIM-002 package is a terminal failure for this installation attempt that is
    /// latched and reported once instead of being re-read every physics tick.
    /// </summary>
    private bool TryInstallAfterInstallers()
    {
        Node? npc = NPC ?? GetParent()?.GetNodeOrNull<Node>("NavigationTestNpc");
        CharacterIK? characterIK = npc?.GetNodeOrNull<CharacterIK>("CharacterIK");
        NPCNavigation? navigation = npc?.GetNodeOrNull<NPCNavigation>("Navigation");
        if (npc is not Node3D actor
            || characterIK?.LeftFootIKTarget is null
            || characterIK.RightFootIKTarget is null
            || navigation is null)
        {
            return false;
        }

        try
        {
            string catalogueJson = Godot.FileAccess.GetFileAsString(navigation.CataloguePath);
            if (string.IsNullOrWhiteSpace(catalogueJson))
            {
                LatchTerminalPackageFailure(navigation.CataloguePath, "the ANIM-002 runtime package catalogue is missing or empty");
                return false;
            }

            _catalogue = StandingLocomotionCatalogue.Parse(catalogueJson, navigation.CataloguePath);
            Coordinator = ContactLockProductionComposition.LoadCoordinator(_catalogue, navigation.CataloguePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            LatchTerminalPackageFailure(navigation.CataloguePath, exception.Message);
            return false;
        }

        _navigation = navigation;
        _characterIK = characterIK;
        _binding = ContactLockProductionComposition.Install(actor, characterIK, Coordinator);
        LeftProvider = _binding.LeftProvider;
        RightProvider = _binding.RightProvider;
        navigation.ContactLockCoordinator = Coordinator;
        return true;
    }

    private void LatchTerminalPackageFailure(string cataloguePath, string detail)
    {
        IsTerminalPackageFailureLatched = true;
        TerminalPackageFailureDiagnosticCount++;
        if (GameLoggerResolver.TryResolve(out ILogger<NPCContactLockPlaytestComposition>? logger) && logger is not null)
        {
            logger.LogError(
                "NPC contact-lock composition disabled for this installation attempt "
                + "(ANIM-002 package '{CataloguePath}'): {Detail}. "
                + "The failing package is not re-read until a new installation attempt.",
                cataloguePath,
                detail);
        }
    }
}

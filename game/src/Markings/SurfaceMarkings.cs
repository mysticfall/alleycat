using Godot;

namespace AlleyCat.Markings;

/// <summary>
/// Applies authored surface markings to one surface of a target mesh by baking marking images
/// into per-instance copies of the surface's base maps. The component duplicates the surface's
/// active material (whole-instance override, surface override, or mesh surface material),
/// bakes each stamped channel into an RGBA8 image with mipmaps, and replaces every material
/// texture slot whose current value is exactly the channel's base texture — no slot-name
/// assumptions, so <see cref="StandardMaterial3D"/> and shader materials work unchanged.
/// </summary>
/// <remarks>
/// The target mesh is either authored explicitly (<see cref="TargetMesh"/>) or discovered at
/// bake time among the parent node's <see cref="MeshInstance3D"/> descendants by albedo-base
/// texture identity, so the same component installs across character topologies with
/// differently-named body meshes. Shared imported resources are never mutated: characters (or
/// surfaces) that share the base material stay unmarked unless they carry their own component
/// instance. Authoring no overlay or no enabled markings is a strict no-op — no material
/// duplication and no mutation.
/// </remarks>
[Tool]
[GlobalClass]
public partial class SurfaceMarkings : Node
{
    private const int PreviewDebounceMsec = 300;

    private static readonly MarkingChannel[] _channels =
    [
        MarkingChannel.Albedo,
        MarkingChannel.Normal,
        MarkingChannel.Roughness,
    ];

    /// <summary>
    /// Gets or sets the mesh instance whose surface receives the markings. When null, the
    /// target is discovered at bake time: the single mesh-surface pair among the
    /// <see cref="MeshInstance3D"/> descendants of this component's parent node (this
    /// component excluded) whose active material references the overlay's
    /// <see cref="MarkingOverlay.AlbedoBase"/> texture by exact resource identity. Zero or
    /// multiple matches fail validation loudly and leave every surface unmarked. An explicit
    /// target keeps its authored <see cref="SurfaceIndex"/>; discovery uses the surface whose
    /// material matched.
    /// </summary>
    [ExportGroup("Targets")]
    [Export]
    public MeshInstance3D? TargetMesh
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the surface of <see cref="TargetMesh"/> the overlay bakes into.
    /// </summary>
    [Export]
    public int SurfaceIndex
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the authored marking overlay. The channel bases must reference the exact
    /// textures the target material's slots hold, because slots are discovered by identity.
    /// </summary>
    [ExportGroup("Markings")]
    [Export]
    public MarkingOverlay? Overlay
    {
        get;
        set;
    }

    private bool _applied;
    private MarkingOverlay? _runtimeOverlay;
    private Material? _installedMaterial;
    private bool _installedOnInstanceOverride;
    private MeshInstance3D? _installedOnTarget;
    private int _installedOnSurfaceIndex;
    private Material? _originalActiveMaterial;
    private Material? _originalSurfaceOverride;
    private readonly List<(StringName Property, MarkingChannel Channel)> _slotAssignments = [];
    private readonly Dictionary<(Texture2D Texture, MarkingChannel Channel), MarkingBuffer> _bufferCache = [];
    private readonly List<Resource> _previewWatched = [];
    private readonly List<Resource> _previewGraphScratch = [];
    private MeshInstance3D? _previewTargetMesh;
    private int _previewSurfaceIndex;
    private bool _previewDirty;
    private ulong _previewChangeMsec;

    /// <summary>
    /// Gets or sets an optional intercept for the editor preview's diagnostics (bake
    /// summaries and authored-state errors). When null, diagnostics go to the editor-visible
    /// warning/error channels. Editor-only reporting; the runtime bake path keeps its
    /// exception contracts.
    /// </summary>
    internal Action<string, bool>? PreviewDiagnosticSink
    {
        get;
        set;
    }

    private bool PreviewReportingEnabled => Engine.IsEditorHint() || PreviewDiagnosticSink is not null;

    /// <inheritdoc />
    public override void _Ready()
    {
        try
        {
            Apply();
        }
        catch (InvalidOperationException ex)
        {
            // A marking authoring mistake (a base matching no material slot, an ambiguous
            // discovery, a half-authored layer) must never abort the host character's
            // installation or scene load: report loudly and stay unmarked.
            ReportPreviewDiagnostic(ex.Message, isError: true);
        }

        if (Engine.IsEditorHint())
        {
            WirePreviewSignals();
        }
        else
        {
            SetProcess(false);
        }
    }

    /// <summary>
    /// Installs the baked marking material. Authoring no overlay or no enabled markings is a
    /// strict no-op, and a successful installation makes later calls no-ops so the surface is
    /// never baked twice from <c>_Ready</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the authored state is invalid —
    /// the validation runs before any mesh or material mutation.</exception>
    public void Apply()
    {
        if (_applied || !RequiresBake(Overlay))
        {
            return;
        }

        _runtimeOverlay = DuplicateOverlay(Overlay!);
        BakeAndInstall();
    }

    /// <summary>
    /// Adds a marking to the named layer and re-bakes. The mutation applies to this instance's
    /// runtime copy of the overlay only; the authored resource stays shared and unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no overlay is authored, the
    /// layer does not exist, or the added marking fails validation.</exception>
    public void AddToLayer(string layerName, SurfaceMarking marking)
    {
        if (string.IsNullOrWhiteSpace(layerName))
        {
            throw new InvalidOperationException(
                $"{Describe()} requires a non-empty layer name for runtime marking mutation, but found '{layerName ?? "null"}'.");
        }

        ArgumentNullException.ThrowIfNull(marking);

        _ = ResolveRuntimeOverlayForMutation();
        MarkingLayer layer = FindLayer(layerName);
        layer.Markings = [.. layer.Markings, marking];
        RebakeAfterMutation();
    }

    /// <summary>
    /// Removes every marking from the named layer and re-bakes from the pristine base maps.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no overlay is authored or the
    /// layer does not exist.</exception>
    public void ClearLayer(string layerName)
    {
        _ = ResolveRuntimeOverlayForMutation();
        FindLayer(layerName).Markings = [];
        RebakeAfterMutation();
    }

    /// <summary>
    /// Enables or disables the named layer and re-bakes from the pristine base maps.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no overlay is authored or the
    /// layer does not exist.</exception>
    public void SetLayerEnabled(string layerName, bool enabled)
    {
        _ = ResolveRuntimeOverlayForMutation();
        FindLayer(layerName).Enabled = enabled;
        RebakeAfterMutation();
    }

    /// <summary>
    /// Editor-only debounced re-bake. Authored resource changes mark the preview dirty and
    /// the re-bake happens once authoring settles. Each editor frame first re-wires the
    /// <c>Changed</c> subscriptions whenever the authored overlay graph's resource identities
    /// changed — a fresh overlay assigned to <see cref="Overlay"/>, or layers or markings
    /// added, removed, or replaced — because <c>Changed</c> only fires from resources already
    /// wired; re-pointed <see cref="TargetMesh"/>/<see cref="SurfaceIndex"/> exports are
    /// detected the same way. Disabled at game runtime.
    /// </summary>
    public override void _Process(double delta)
    {
        PollPreviewGraph();

        if (!_previewDirty || Time.GetTicksMsec() - _previewChangeMsec < PreviewDebounceMsec)
        {
            return;
        }

        _previewDirty = false;
        RebuildPreview();
    }

    /// <summary>
    /// Editor save/exit hygiene: a baked preview override must never be saved into the scene,
    /// so it is stripped before saves and node exit and restored after saves.
    /// </summary>
    public override void _Notification(int what)
    {
        base._Notification(what);
        if (!Engine.IsEditorHint())
        {
            return;
        }

        switch (what)
        {
            case (int)NotificationEditorPreSave:
                StripInstalledMaterial();
                break;
            case (int)NotificationEditorPostSave:
                RestoreInstalledMaterial();
                break;
            default:
                break;
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (Engine.IsEditorHint())
        {
            UnwirePreviewSignals();
            StripInstalledMaterial();
        }

        base._ExitTree();
    }

    private static bool RequiresBake(MarkingOverlay? overlay)
    {
        if (overlay is null)
        {
            return false;
        }

        foreach (MarkingLayer layer in overlay.Layers)
        {
            if (layer is { Enabled: true } && SuppliesAnyMarking(layer))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SuppliesAnyMarking(MarkingLayer layer)
    {
        foreach (SurfaceMarking marking in layer.Markings)
        {
            if (marking is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ChannelSuppliesStamps(MarkingOverlay overlay, MarkingChannel channel)
    {
        foreach (MarkingLayer layer in overlay.Layers)
        {
            if (layer is not { Enabled: true })
            {
                continue;
            }

            foreach (SurfaceMarking marking in layer.Markings)
            {
                if (marking is not null && GetChannelTexture(marking, channel) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Texture2D? GetChannelTexture(SurfaceMarking marking, MarkingChannel channel)
        => channel switch
        {
            MarkingChannel.Albedo => marking.AlbedoTexture,
            MarkingChannel.Normal => marking.NormalTexture,
            MarkingChannel.Roughness => marking.RoughnessTexture,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown marking channel."),
        };

    private static Texture2D? GetChannelBase(MarkingOverlay overlay, MarkingChannel channel)
        => channel switch
        {
            MarkingChannel.Albedo => overlay.AlbedoBase,
            MarkingChannel.Normal => overlay.NormalBase,
            MarkingChannel.Roughness => overlay.RoughnessBase,
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown marking channel."),
        };

    private (MeshInstance3D Mesh, int SurfaceIndex) ValidateAuthoredState()
    {
        _ = _runtimeOverlay
            ?? throw new InvalidOperationException(
                $"{Describe()} requires {nameof(Overlay)} to reference a non-null {nameof(MarkingOverlay)} when markings are baked, but found none.");

        (MeshInstance3D target, int surfaceIndex) = ResolveInstallTarget();

        _ = target.GetActiveMaterial(surfaceIndex)
            ?? throw new InvalidOperationException(
                $"{Describe()} requires the resolved target mesh '{target.GetPath()}' surface {surfaceIndex} to have a material to bake markings onto, but found none.");

        ValidateOverlayContent();

        return (target, surfaceIndex);
    }

    private void ValidateOverlayContent()
    {
        bool suppliesAlbedo = false;
        bool suppliesNormal = false;
        bool suppliesRoughness = false;
        HashSet<string> layerNames = new(StringComparer.Ordinal);
        for (int layerIndex = 0; layerIndex < _runtimeOverlay!.Layers.Length; layerIndex++)
        {
            MarkingLayer layer = _runtimeOverlay.Layers[layerIndex]
                ?? throw new InvalidOperationException(
                    $"{Describe()} has a null layer entry at index {layerIndex}.");

            if (!layerNames.Add(layer.Name))
            {
                throw new InvalidOperationException(
                    $"{Describe()} has duplicate layer name '{layer.Name}'. Layer names must be ordinally unique.");
            }

            for (int index = 0; index < layer.Markings.Length; index++)
            {
                SurfaceMarking marking = layer.Markings[index]
                    ?? throw new InvalidOperationException(
                        $"{Describe()} layer '{layer.Name}' has a null marking entry at index {index}.");

                suppliesAlbedo |= marking.AlbedoTexture is not null;
                suppliesNormal |= marking.NormalTexture is not null;
                suppliesRoughness |= marking.RoughnessTexture is not null;
                ValidateMarking(marking, layer.Name, index);
            }
        }

        if (suppliesAlbedo && _runtimeOverlay.AlbedoBase is null)
        {
            throw new InvalidOperationException(
                $"{Describe()} has markings supplying an albedo overlay, but {nameof(MarkingOverlay.AlbedoBase)} is null. Author the albedo base map the target material already uses.");
        }

        if (suppliesNormal && _runtimeOverlay.NormalBase is null)
        {
            throw new InvalidOperationException(
                $"{Describe()} has markings supplying a normal overlay, but {nameof(MarkingOverlay.NormalBase)} is null. Author the normal base map the target material already uses.");
        }

        if (suppliesRoughness && _runtimeOverlay.RoughnessBase is null)
        {
            throw new InvalidOperationException(
                $"{Describe()} has markings supplying a roughness overlay, but {nameof(MarkingOverlay.RoughnessBase)} is null. Author the roughness base map the target material already uses.");
        }
    }

    /// <summary>
    /// Resolves the mesh and surface the bake installs onto: the explicitly authored
    /// <see cref="TargetMesh"/>/<see cref="SurfaceIndex"/> when set, otherwise the single
    /// discovered mesh-surface pair whose active material references the overlay's albedo
    /// base by identity.
    /// </summary>
    private (MeshInstance3D Mesh, int SurfaceIndex) ResolveInstallTarget()
    {
        if (TargetMesh is not { } explicitTarget)
        {
            return DiscoverInstallTarget();
        }

        if (SurfaceIndex < 0)
        {
            throw new InvalidOperationException(
                $"{Describe()} requires {nameof(SurfaceIndex)} to be non-negative, but found {SurfaceIndex}.");
        }

        int surfaceCount = explicitTarget.Mesh?.GetSurfaceCount() ?? 0;
        return surfaceCount <= SurfaceIndex
            ? throw new InvalidOperationException(
                $"{Describe()} requires {nameof(SurfaceIndex)} {SurfaceIndex} to reference a surface of {nameof(TargetMesh)} '{explicitTarget.GetPath()}', but its mesh has {surfaceCount} surface(s).")
            : ((MeshInstance3D Mesh, int SurfaceIndex))(explicitTarget, SurfaceIndex);
    }

    /// <summary>
    /// Discovers the install target among the <see cref="MeshInstance3D"/> descendants of
    /// this component's parent node (this component excluded): a mesh surface matches when
    /// its active material — same precedence as <see cref="MeshInstance3D.GetActiveMaterial"/>
    /// — references the overlay's albedo base texture by exact resource identity, using the
    /// same texture-typed property walk as slot replacement. Exactly one match resolves;
    /// anything else is a loud validation failure.
    /// </summary>
    private (MeshInstance3D Mesh, int SurfaceIndex) DiscoverInstallTarget()
    {
        Node parent = GetParent()
            ?? throw new InvalidOperationException(
                $"{Describe()} cannot discover a target mesh while {nameof(TargetMesh)} is unset because the component is not inside the scene tree. Author {nameof(TargetMesh)} explicitly.");

        Texture2D anchor = _runtimeOverlay!.AlbedoBase
            ?? throw new InvalidOperationException(
                $"{Describe()} cannot discover a target mesh while {nameof(TargetMesh)} is unset and {nameof(MarkingOverlay.AlbedoBase)} is null. Author the albedo base map the target material already uses, or author {nameof(TargetMesh)} explicitly.");

        List<(MeshInstance3D Mesh, int Surface)> matches = [];
        foreach (Node node in parent.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false))
        {
            if (ReferenceEquals(node, this) || node is not MeshInstance3D mesh)
            {
                continue;
            }

            int surfaceCount = mesh.Mesh?.GetSurfaceCount() ?? 0;
            for (int surface = 0; surface < surfaceCount; surface++)
            {
                if (mesh.GetActiveMaterial(surface) is { } active && FindTextureSlots(active, anchor).Count > 0)
                {
                    matches.Add((mesh, surface));
                }
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        string matchList = string.Join(", ", matches.Select(match => $"'{match.Mesh.GetPath()}' surface {match.Surface}"));
        throw new InvalidOperationException(matches.Count == 0
            ? $"{Describe()} has {nameof(TargetMesh)} unset and no mesh surface under '{parent.GetPath()}' whose active material references {nameof(MarkingOverlay.AlbedoBase)} '{anchor.ResourcePath}' by identity. Author {nameof(TargetMesh)} explicitly, or make exactly one mesh surface material reference the overlay's albedo base."
            : $"{Describe()} has {nameof(TargetMesh)} unset and {matches.Count} mesh surfaces under '{parent.GetPath()}' whose active material references {nameof(MarkingOverlay.AlbedoBase)} '{anchor.ResourcePath}' by identity: {matchList}. Discovery requires exactly one match; author {nameof(TargetMesh)} explicitly to disambiguate.");
    }

    private void ValidateMarking(SurfaceMarking marking, string layerName, int index)
    {
        if (marking.AlbedoTexture is null && marking.NormalTexture is null && marking.RoughnessTexture is null)
        {
            throw new InvalidOperationException(
                $"{Describe()} layer '{layerName}' marking at index {index} requires at least one channel texture (albedo, normal, or roughness), but found none.");
        }

        if (!float.IsFinite(marking.Position.X) || !float.IsFinite(marking.Position.Y)
            || !float.IsFinite(marking.RotationDegrees))
        {
            throw new InvalidOperationException(
                $"{Describe()} layer '{layerName}' marking at index {index} requires a finite UV position and rotation, but found position {marking.Position} rotation {marking.RotationDegrees}.");
        }

        if (!float.IsFinite(marking.Scale.X) || !float.IsFinite(marking.Scale.Y)
            || marking.Scale.X <= 0f || marking.Scale.Y <= 0f)
        {
            throw new InvalidOperationException(
                $"{Describe()} layer '{layerName}' marking at index {index} requires a positive, finite scale, but found {marking.Scale}.");
        }

        if (!float.IsFinite(marking.Strength) || marking.Strength < 0f || marking.Strength > 1f)
        {
            throw new InvalidOperationException(
                $"{Describe()} layer '{layerName}' marking at index {index} requires a finite strength in 0..1, but found {marking.Strength}.");
        }

        if (!IsFiniteColor(marking.Tint) || marking.Tint.R < 0f || marking.Tint.G < 0f || marking.Tint.B < 0f)
        {
            throw new InvalidOperationException(
                $"{Describe()} layer '{layerName}' marking at index {index} requires a finite tint with non-negative components, but found {marking.Tint}.");
        }
    }

    private static bool IsFiniteColor(Color color)
        => float.IsFinite(color.R) && float.IsFinite(color.G) && float.IsFinite(color.B) && float.IsFinite(color.A);

    private void BakeAndInstall()
    {
        if (!RequiresBake(_runtimeOverlay))
        {
            return;
        }

        (MeshInstance3D target, int surfaceIndex) = ValidateAuthoredState();
        Material activeMaterial = target.GetActiveMaterial(surfaceIndex)!;
        Material instanceMaterial = activeMaterial.Duplicate() as Material
            ?? throw new InvalidOperationException(
                $"{Describe()} failed to duplicate {activeMaterial.GetType().FullName} for the target mesh '{target.GetPath()}' surface {surfaceIndex}.");

        List<(StringName Property, MarkingChannel Channel)> assignments = [];
        foreach (MarkingChannel channel in _channels)
        {
            if (!ChannelSuppliesStamps(_runtimeOverlay!, channel))
            {
                continue;
            }

            Texture2D baseTexture = GetChannelBase(_runtimeOverlay!, channel)!;
            MarkingBuffer working = LoadBuffer(baseTexture, channel).Duplicate();
            StampChannel(working, _runtimeOverlay!, channel);
            var bakedTexture = ImageTexture.CreateFromImage(
                MarkingImageAdapter.ToImage(working, channel));

            List<StringName> slots = FindTextureSlots(instanceMaterial, baseTexture);
            if (slots.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{Describe()} requires the {channel} base texture '{baseTexture.ResourcePath}' to be referenced by at least one texture slot of the active material '{activeMaterial.ResourcePath}', but no slot holds that exact texture. Author the channel bases to the textures the material already uses.");
            }

            foreach (StringName slot in slots)
            {
                instanceMaterial.Set(slot, bakedTexture);
                assignments.Add((slot, channel));
            }
        }

        // Record the install target with the originals: strip/restore must always operate
        // on the mesh and surface the bake actually installed on, never on whatever
        // TargetMesh/SurfaceIndex currently reference — the author may re-point them while
        // a preview is installed, and discovery may have resolved the pair.
        _originalActiveMaterial = activeMaterial;
        _installedOnTarget = target;
        _installedOnSurfaceIndex = surfaceIndex;
        _installedOnInstanceOverride = target.MaterialOverride is not null;
        _originalSurfaceOverride = target.GetSurfaceOverrideMaterial(surfaceIndex);
        if (_installedOnInstanceOverride)
        {
            target.MaterialOverride = instanceMaterial;
        }
        else
        {
            target.SetSurfaceOverrideMaterial(surfaceIndex, instanceMaterial);
        }

        _installedMaterial = instanceMaterial;
        _slotAssignments.Clear();
        _slotAssignments.AddRange(assignments);
        _applied = true;

        if (PreviewReportingEnabled)
        {
            // Identity replacement is observable: report how many texture slots each
            // stamp-supplying channel replaced. A channel base matching zero slots can never
            // be a silent no-op — it throws above and the preview path reports the error.
            string counts = string.Join(
                ", ",
                _channels
                    .Where(channel => assignments.Any(assignment => assignment.Channel == channel))
                    .Select(channel =>
                        $"{channel.ToString().ToLowerInvariant()}: {assignments.Count(assignment => assignment.Channel == channel)} slot(s)"));
            ReportPreviewDiagnostic(
                $"{Describe()} installed a baked marking material ({counts} replaced by identity).", isError: false);
        }
    }

    private void ReBakeChannels()
    {
        foreach (MarkingChannel channel in _channels)
        {
            // Re-bake every installed channel — including channels whose stamps have all been
            // cleared or disabled since: their slots must fall back to the pristine base copy.
            bool installed = false;
            foreach ((_, MarkingChannel slotChannel) in _slotAssignments)
            {
                installed |= slotChannel == channel;
            }

            if (!installed)
            {
                continue;
            }

            Texture2D baseTexture = GetChannelBase(_runtimeOverlay!, channel)!;
            MarkingBuffer working = LoadBuffer(baseTexture, channel).Duplicate();
            if (ChannelSuppliesStamps(_runtimeOverlay!, channel))
            {
                StampChannel(working, _runtimeOverlay!, channel);
            }
            var bakedTexture = ImageTexture.CreateFromImage(
                MarkingImageAdapter.ToImage(working, channel));

            foreach ((StringName property, MarkingChannel slotChannel) in _slotAssignments)
            {
                if (slotChannel == channel)
                {
                    _installedMaterial!.Set(property, bakedTexture);
                }
            }
        }
    }

    private void StampChannel(MarkingBuffer buffer, MarkingOverlay overlay, MarkingChannel channel)
    {
        List<MarkingLayerPlan> plans = [];
        foreach (MarkingLayer layer in overlay.Layers)
        {
            if (layer is null)
            {
                continue;
            }

            List<MarkingStamp> stamps = [];
            foreach (SurfaceMarking marking in layer.Markings)
            {
                if (marking is null || GetChannelTexture(marking, channel) is not { } texture)
                {
                    continue;
                }

                if (MarkingImageAdapter.TryCreateStamp(marking, channel, LoadBuffer(texture, channel)) is { } stamp)
                {
                    stamps.Add(stamp);
                }
            }

            plans.Add(new MarkingLayerPlan(layer.Enabled, [.. stamps]));
        }

        MarkingCompositor.StampLayers(buffer, plans, channel);
    }

    private MarkingBuffer LoadBuffer(Texture2D texture, MarkingChannel channel)
    {
        (Texture2D, MarkingChannel) key = (texture, channel);
        if (_bufferCache.TryGetValue(key, out MarkingBuffer? cached))
        {
            return cached;
        }

        Image image = texture.GetImage()
            ?? throw new InvalidOperationException(
                $"{Describe()} requires the texture '{texture.ResourcePath}' to expose image data for baking, but {texture.GetType().FullName}.GetImage() returned null.");

        MarkingBuffer buffer = MarkingImageAdapter.ToBuffer(image, channel);
        _bufferCache[key] = buffer;
        return buffer;
    }

    private static List<StringName> FindTextureSlots(Material material, Texture2D texture)
    {
        List<StringName> slots = [];
        foreach (Godot.Collections.Dictionary property in material.GetPropertyList())
        {
            if ((Variant.Type)(int)property["type"] != Variant.Type.Object)
            {
                continue;
            }

            StringName name = property["name"].AsStringName();
            if (material.Get(name).AsGodotObject() is Texture2D value && ReferenceEquals(value, texture))
            {
                slots.Add(name);
            }
        }

        return slots;
    }

    private MarkingOverlay ResolveRuntimeOverlayForMutation()
        => _runtimeOverlay ??= Overlay is null
            ? throw new InvalidOperationException(
                $"{Describe()} requires {nameof(Overlay)} to be authored before runtime layer mutation, but found null.")
            : DuplicateOverlay(Overlay);

    private MarkingLayer FindLayer(string layerName)
        => _runtimeOverlay!.Layers.FirstOrDefault(layer => layer.Name == layerName)
            ?? throw new InvalidOperationException(
                $"{Describe()} has no layer named '{layerName}'. Runtime layer mutation requires the layer to be authored on the overlay.");

    private void RebakeAfterMutation()
    {
        if (!_applied)
        {
            BakeAndInstall();
            return;
        }

        _ = ValidateAuthoredState();
        ReBakeChannels();
    }

    private void ResetToUnmarked()
    {
        StripInstalledMaterial();
        _installedMaterial = null;
        _installedOnTarget = null;
        _slotAssignments.Clear();
        _applied = false;
        _runtimeOverlay = null;
    }

    private void StripInstalledMaterial()
    {
        if (!HasLiveInstalledPreview())
        {
            return;
        }

        if (_installedOnInstanceOverride)
        {
            _installedOnTarget!.MaterialOverride = _originalActiveMaterial;
        }
        else
        {
            _installedOnTarget!.SetSurfaceOverrideMaterial(_installedOnSurfaceIndex, _originalSurfaceOverride);
        }
    }

    private void RestoreInstalledMaterial()
    {
        if (!HasLiveInstalledPreview())
        {
            return;
        }

        if (_installedOnInstanceOverride)
        {
            _installedOnTarget!.MaterialOverride = _installedMaterial;
        }
        else
        {
            _installedOnTarget!.SetSurfaceOverrideMaterial(_installedOnSurfaceIndex, _installedMaterial);
        }
    }

    private bool HasLiveInstalledPreview()
        // The install-time target may have been deleted from the scene since the bake; a
        // freed mesh can be neither stripped nor restored, so the preview is simply gone.
        => _installedMaterial is not null
            && _installedOnTarget is not null
            && IsInstanceValid(_installedOnTarget);

    /// <summary>
    /// Re-bakes the editor preview from the currently authored overlay. Authoring states are
    /// transiently invalid all the time (a base not yet assigned, a half-built layer), so
    /// authored-state failures are reported as editor-visible errors and the surface is left
    /// unmarked instead of throwing out of <c>_Process</c>.
    /// </summary>
    internal void RebuildPreview()
    {
        ResetToUnmarked();
        try
        {
            if (RequiresBake(Overlay))
            {
                _runtimeOverlay = DuplicateOverlay(Overlay!);
                BakeAndInstall();
            }
        }
        catch (InvalidOperationException ex)
        {
            ReportPreviewDiagnostic(ex.Message, isError: true);
        }

        WirePreviewSignals();
    }

    /// <summary>
    /// Re-wires the preview subscriptions whenever the authored overlay graph's resource
    /// identities changed — a new overlay assigned, layers or markings added, removed, or
    /// replaced — and marks the preview dirty so the change re-bakes after the debounce.
    /// A re-pointed <see cref="TargetMesh"/> or changed <see cref="SurfaceIndex"/> is treated
    /// the same way: any installed preview is stripped from its install-time target first
    /// (restoring that target properly), then the debounced rebuild bakes onto the new
    /// target. No-op while the wired graph and targets match the authored state.
    /// </summary>
    internal void PollPreviewGraph()
    {
        bool targetChanged = !ReferenceEquals(_previewTargetMesh, TargetMesh) || _previewSurfaceIndex != SurfaceIndex;
        if (targetChanged)
        {
            ResetToUnmarked();
        }

        CollectAuthoredGraph(_previewGraphScratch);
        bool current = !targetChanged && _previewGraphScratch.Count == _previewWatched.Count;
        for (int index = 0; current && index < _previewGraphScratch.Count; index++)
        {
            current &= ReferenceEquals(_previewGraphScratch[index], _previewWatched[index]);
        }

        _previewGraphScratch.Clear();
        if (current)
        {
            return;
        }

        WirePreviewSignals();
        MarkPreviewDirty();
    }

    private void CollectAuthoredGraph(List<Resource> sink)
    {
        if (Overlay is null)
        {
            return;
        }

        sink.Add(Overlay);
        foreach (MarkingLayer layer in Overlay.Layers)
        {
            if (layer is null)
            {
                continue;
            }

            sink.Add(layer);
            foreach (SurfaceMarking marking in layer.Markings)
            {
                if (marking is not null)
                {
                    sink.Add(marking);
                }
            }
        }
    }

    private void WirePreviewSignals()
    {
        UnwirePreviewSignals();
        CollectAuthoredGraph(_previewGraphScratch);
        foreach (Resource resource in _previewGraphScratch)
        {
            WatchPreviewResource(resource);
        }

        _previewGraphScratch.Clear();
        // The wiring snapshot records the install targets the preview state was built for,
        // so the poll can detect later re-points of TargetMesh/SurfaceIndex.
        _previewTargetMesh = TargetMesh;
        _previewSurfaceIndex = SurfaceIndex;
    }

    private void WatchPreviewResource(Resource resource)
    {
        resource.Changed += HandlePreviewResourceChanged;
        _previewWatched.Add(resource);
    }

    private void UnwirePreviewSignals()
    {
        foreach (Resource resource in _previewWatched)
        {
            resource.Changed -= HandlePreviewResourceChanged;
        }

        _previewWatched.Clear();
    }

    private void HandlePreviewResourceChanged()
        => MarkPreviewDirty();

    private void MarkPreviewDirty()
    {
        _previewDirty = true;
        _previewChangeMsec = Time.GetTicksMsec();
    }

    private void ReportPreviewDiagnostic(string message, bool isError)
    {
        if (PreviewDiagnosticSink is { } sink)
        {
            sink(message, isError);
            return;
        }

        if (isError)
        {
            GD.PushError(message);
        }
        else
        {
            GD.PushWarning(message);
        }
    }

    private static MarkingOverlay DuplicateOverlay(MarkingOverlay source)
    {
        MarkingOverlay copy = new()
        {
            AlbedoBase = source.AlbedoBase,
            NormalBase = source.NormalBase,
            RoughnessBase = source.RoughnessBase,
            Layers = [.. source.Layers.Select(DuplicateLayer)]
        };
        return copy;
    }

    private static MarkingLayer DuplicateLayer(MarkingLayer source)
    {
        MarkingLayer copy = new()
        {
            Name = source.Name,
            Enabled = source.Enabled,
            Markings = [.. source.Markings.Select(DuplicateMarking)]
        };
        return copy;
    }

    private static SurfaceMarking DuplicateMarking(SurfaceMarking source) => new()
    {
        Position = source.Position,
        RotationDegrees = source.RotationDegrees,
        Scale = source.Scale,
        AlbedoTexture = source.AlbedoTexture,
        NormalTexture = source.NormalTexture,
        RoughnessTexture = source.RoughnessTexture,
        Tint = source.Tint,
        Strength = source.Strength,
        AlbedoBlend = source.AlbedoBlend,
        RoughnessBlend = source.RoughnessBlend,
    };

    private string Describe()
        => IsInsideTree() ? $"SurfaceMarkings node '{GetPath()}'" : $"SurfaceMarkings node '{Name}'";
}

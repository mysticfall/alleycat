using AlleyCat.Common;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

public partial class PlayerMotionBricks
{
    /// <summary>Show the real submitted target[0] root, not an extrapolated destination. Independent of IK diagnostics.</summary>
    [Export]
    public bool ShowSubmittedTarget
    {
        get; set;
    }

    private Node3D? _activeMarker, _pendingMarker;
    private Node3D? _virtualMarker;
    private Transform3D? _activeTargetWorld, _pendingTargetWorld;
    private long _pendingMarkerTicket;
    private string _activeMarkerCaption = "", _pendingMarkerCaption = "";
    private bool _continuousMarkersDirty, _continuousMarkersShown;

    private void SetTargetDiagnostic(string status)
    {
        if (status == "")
        {
            _activeTargetWorld = _pendingTargetWorld = null;
            _virtualMarker?.Hide();
            _continuousMarkersDirty = true;
        }
        RefreshTargetDiagnostic();
    }

    private void RefreshTargetDiagnostic() => RefreshContinuousMarkers();

    private void SetContinuousPending(long ticket, Transform3D target)
    {
        _pendingMarkerTicket = ticket;
        _pendingTargetWorld = WorldTarget(target);
        _pendingMarkerCaption = $"SUBMITTED pending #{ticket}\nGeneration guide (+Z forward)";
        _continuousMarkersDirty = true;
        if (ShowSubmittedTarget)
        {
            _logger!.LogInformation("Continuous pending target {Request}: {Target}, +Z {Heading}; actor {Actor}, active {Active}",
                ticket, _pendingTargetWorld.Value.Origin, _pendingTargetWorld.Value.Basis.Z, _actor!.GlobalPosition, LastAppliedRequest);
        }
        RefreshContinuousMarkers();
    }

    private void StartContinuousMarker(long ticket)
    {
        if (_pendingTargetWorld is null || _pendingMarkerTicket != ticket)
        {
            throw new InvalidOperationException("Continuous marker ownership differs from consumed request.");
        }
        _activeTargetWorld = _pendingTargetWorld;
        _pendingMarkerCaption = $"SUBMITTED consumed #{ticket}\nGeneration guide (+Z forward)";
        _activeMarkerCaption = $"ACTIVE playing #{ticket}\nGeneration guide (+Z forward)";
        _continuousMarkersDirty = true;
        RefreshContinuousMarkers();
    }

    private void RefreshContinuousMarkers()
    {
        if (!ShowSubmittedTarget)
        {
            _activeMarker?.Hide();
            _pendingMarker?.Hide();
            _virtualMarker?.Hide();
            _continuousMarkersShown = false;
            return;
        }
        if (!_continuousMarkersDirty && _continuousMarkersShown)
        {
            return;
        }
        UpdateMarker(ref _activeMarker, "ActiveMotionTarget", _activeTargetWorld, _activeMarkerCaption, new Color(0.05f, 0.15f, 0.4f));
        UpdateMarker(ref _pendingMarker, "PendingMotionTarget", _pendingTargetWorld, _pendingMarkerCaption, Colors.DarkOrange);
        _continuousMarkersDirty = false;
        _continuousMarkersShown = true;
    }

    private void RefreshVirtualMarker()
    {
        if (!ShowSubmittedTarget || ContinuousDriver?.VirtualTarget is not { } target)
        {
            _virtualMarker?.Hide();
            return;
        }
        // Constant caption and cached node: no per-frame string construction or scene-tree lookup.
        if (_virtualMarker is null)
        {
            UpdateMarker(ref _virtualMarker, "VirtualMotionTarget", WorldTarget(target),
                "VIRTUAL cycle prediction\nNext comparison (no request ID)", new Color(0, 0.3f, 0.08f));
        }
        _virtualMarker!.GlobalTransform = WorldTarget(target);
        _virtualMarker.Show();
    }

    private void UpdateMarker(ref Node3D? marker, string name, Transform3D? target, string caption, Color colour)
    {
        if (target is null)
        {
            marker?.Hide();
            return;
        }
        if (marker is null)
        {
            marker = GD.Load<PackedScene>("res://assets/markers/debug_marker.tscn").Instantiate<Node3D>();
            marker.Name = name;
            marker.TopLevel = true;
            marker.Set("label_text", caption);
            AddChild(marker);
            Label3D label = marker.RequireNode<Label3D>("Label3D");
            label.PixelSize = 0.002f;
            label.FontSize = 28;
            label.Modulate = colour;
            label.OutlineModulate = Colors.White;
            label.OutlineSize = 3;
            if (name == "ActiveMotionTarget")
            {
                label.Position += new Vector3(0, 0.24f, 0);
            }
            else if (name == "VirtualMotionTarget")
            {
                label.Position += new Vector3(0, 0.48f, 0);
            }
        }
        marker.GlobalTransform = target.Value;
        marker.RequireNode<Label3D>("Label3D").Text = caption;
        marker.Show();
    }
}

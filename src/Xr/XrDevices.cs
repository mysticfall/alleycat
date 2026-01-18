using Godot;

namespace AlleyCat.Xr;

public readonly record struct XrTrackers(
    XRController3D RightHand,
    XRController3D LeftHand
);

public readonly record struct XrDevices(
    XRInterface Interface,
    XROrigin3D Origin,
    XRCamera3D Camera,
    XrTrackers Trackers
);
using System.Text.RegularExpressions;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>Source-level guard for the production finger-retargeting anatomical paths and write boundary.</summary>
public sealed class OpticalFingerTrackingModifierSourceGuardTests
{
    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProcessHandRetargetsNonThumbJointsThroughConstrainedAnatomicalMapping()
    {
        string processHand = ReadProcessHandSource();

        Assert.Contains("Quaternion sourceRelation = _sourceRelations[fingerIndex];", processHand, StringComparison.Ordinal);
        Assert.Contains("FingerAnatomicalMath.TryMapProximalDestination(", processHand, StringComparison.Ordinal);
        Assert.Contains("FingerAnatomicalMath.TryMapHingeDestination(", processHand, StringComparison.Ordinal);
        Assert.Contains("_localProximalFrames[calibrationIndex]", processHand, StringComparison.Ordinal);
        Assert.Contains("_localHingeAxes[calibrationIndex]", processHand, StringComparison.Ordinal);
        Assert.Contains("_effectiveDestinationNeutrals[calibrationIndex]", processHand, StringComparison.Ordinal);
        Assert.DoesNotContain("calibration.DestinationNeutral", processHand, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProductionMathProhibitsRuntimeBasisCorrespondenceAndGeneralConjugation()
    {
        string processHand = ReadProcessHandSource();
        string modifier = File.ReadAllText(ResolveSourcePath());
        string retargetingMath = File.ReadAllText(ResolveSourcePath("FingerRetargetingMath.cs"));
        string anatomicalMath = File.ReadAllText(ResolveSourcePath("FingerAnatomicalMath.cs"));

        Assert.DoesNotContain("BasisCorrespondence", processHand, StringComparison.Ordinal);
        Assert.DoesNotContain("RetargetNonThumbRotation", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("RetargetNonThumbRotation", retargetingMath, StringComparison.Ordinal);
        Assert.DoesNotContain("basisCorrespondence", retargetingMath, StringComparison.Ordinal);
        Assert.DoesNotContain("correspondence.Inverse()", retargetingMath, StringComparison.Ordinal);
        Assert.DoesNotContain("BasisCorrespondence", anatomicalMath, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProcessHandMapsThumbsThroughConstrainedModelWithoutDirectWrites()
    {
        string processHand = ReadProcessHandSource();

        Assert.Contains("bool mapped = destination.Joint == XRHandJoint.ThumbMetacarpal", processHand, StringComparison.Ordinal);
        Assert.Contains("FingerAnatomicalMath.TryMapThumbMetacarpalSwing(", processHand, StringComparison.Ordinal);
        Assert.Contains("_thumbCorrespondenceFrames[(int)side]", processHand, StringComparison.Ordinal);
        Assert.Contains("_resolvedMetacarpalCalibration[(int)side]", processHand, StringComparison.Ordinal);
        Assert.Contains("destination.Joint is not (XRHandJoint.ThumbMetacarpal or XRHandJoint.ThumbProximal)", processHand, StringComparison.Ordinal);
        Assert.Contains("skeleton.SetBonePoseRotation(_boneIndices[cacheIndex], rotation);", processHand, StringComparison.Ordinal);
        Assert.DoesNotContain("rotation = sourceRelation;", processHand, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProductionRetainsOneSourceRelationPerDestination()
    {
        string modifier = File.ReadAllText(ResolveSourcePath());
        string processHand = ReadProcessHandSource();

        Assert.Contains("private readonly XRHandJointSourceSample[] _jointSamples = new XRHandJointSourceSample[TrackedJointCount];", modifier, StringComparison.Ordinal);
        Assert.Contains("private readonly Quaternion[] _sourceRelations = new Quaternion[FingerBonesPerSide];", modifier, StringComparison.Ordinal);
        Assert.Contains("private readonly bool[] _sourceRelationValid = new bool[FingerBonesPerSide];", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Transform3D[] _jointTransforms", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("bool[] _jointValid", modifier, StringComparison.Ordinal);
        Assert.Contains("RetainSourceRelations(side);", processHand, StringComparison.Ordinal);
        _ = Assert.Single(Regex.Matches(modifier, "FingerRetargetingMath\\.DeriveParentRelativeRotation\\("));
    }

    /// <summary>Guards direct acceptance, both thumb-wrist exceptions, no parent recursion, one provider fetch.</summary>
    [Fact]
    public void SampleAcceptanceUsesDestinationOnlyWithExplicitThumbWristExceptions()
    {
        string modifier = File.ReadAllText(ResolveSourcePath());
        string acceptance = ExtractRegion(modifier, "private bool IsSampleAccepted(", "private static bool IsNonThumbProximal(");

        Assert.Contains("_jointSamples[(int)destination.Joint].ProductionAccepted", acceptance, StringComparison.Ordinal);
        Assert.Contains("XRHandJoint.ThumbMetacarpal or XRHandJoint.ThumbProximal", acceptance, StringComparison.Ordinal);
        Assert.Contains("_jointSamples[(int)XRHandJoint.Wrist].ProductionAccepted", acceptance, StringComparison.Ordinal);
        Assert.DoesNotContain("destination.ParentJoint", acceptance, StringComparison.Ordinal);
        Assert.DoesNotContain("ParentJoint.ProductionAccepted", modifier, StringComparison.Ordinal);
        _ = Assert.Single(Regex.Matches(modifier, "jointProvider\\.TryGetJoint\\("));
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void JointProviderAcceptanceDoesNotPromotePositionFlagsIntoMappingOrFreeze()
    {
        string interfaceSource = File.ReadAllText(ResolveSourcePath("IXRHandJointProvider.cs"));
        string openXR = File.ReadAllText(ResolveOpenXRSourcePath());
        string mock = File.ReadAllText(ResolveMockXRSourcePath());

        Assert.Contains("bool PositionValid,", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("bool PositionTracked,", interfaceSource, StringComparison.Ordinal);
        Assert.Contains("bool ProductionAccepted,", interfaceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PositionValid", ExtractRegion(openXR, "private static XRHandJointSourceRejection ClassifyJointSample(", "private static bool IsJointActivelyTracked("), StringComparison.Ordinal);
        Assert.DoesNotContain("PositionTracked", ExtractRegion(mock, "private XRHandJointSourceRejection ClassifyJointSample(", "private static bool IsOrientationUsable("), StringComparison.Ordinal);
        Assert.DoesNotContain("PositionValid", ReadProcessHandSource(), StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProductionThumbMappingProhibitsRestGeometryFrameAndHingeDerivation()
    {
        string anatomicalMath = File.ReadAllText(ResolveSourcePath("FingerAnatomicalMath.cs"));
        string modifier = File.ReadAllText(ResolveSourcePath());

        Assert.DoesNotContain("TryDeriveThumbFrame", anatomicalMath, StringComparison.Ordinal);
        Assert.DoesNotContain("FingerThumbFrameQuality", anatomicalMath, StringComparison.Ordinal);
        Assert.DoesNotContain("_thumbFrames", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("_thumbFrameQuality", modifier, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProcessHandDoesNotReintroduceTemporalDeltaOrFrameMapSchemes()
    {
        string processHand = ReadProcessHandSource();

        Assert.DoesNotContain("FrameMap", processHand, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldDelta", processHand, StringComparison.Ordinal);
        Assert.DoesNotContain("EntryCorrespondence", processHand, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void ProductionMathRetainsExactAnatomicalEquations()
    {
        string source = File.ReadAllText(ResolveSourcePath("FingerAnatomicalMath.cs"));

        Assert.Contains("thetaRadians = 2.0f * Mathf.Atan2(p / m, rotation.W / m);", source, StringComparison.Ordinal);
        Assert.Contains("rotation = destinationNeutral.Normalized() * new Quaternion(localHingeAxis.Normalized(), theta);", source, StringComparison.Ordinal);
        Assert.Contains("public static bool TryMapThumbMetacarpalSwing(", source, StringComparison.Ordinal);
        Assert.Contains("float spanSign = side == LimbSide.Left ? -1.0f : 1.0f;", source, StringComparison.Ordinal);
        Assert.Contains("float combinedGate = hingeGate * bendGate;", source, StringComparison.Ordinal);
        Assert.Contains("rotation = neutral * appliedSwing;", source, StringComparison.Ordinal);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void AuthoredReferencePathPropertiesUseDirectSampling()
    {
        string modifier = File.ReadAllText(ResolveSourcePath());
        string sampler = File.ReadAllText(ResolveSourcePath("AuthoredThumbReferenceSampler.cs"));

        Assert.Contains("AuthoredThumbReferenceSampler.TrySample(", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNode<AnimationPlayer", sampler, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAnimationLibrary", sampler, StringComparison.Ordinal);
        Assert.DoesNotContain(".Play(", sampler, StringComparison.Ordinal);
        Assert.Contains("ResourceLoader.Load(path)", sampler, StringComparison.Ordinal);
    }

    /// <summary>Guards H9 against reintroducing temporary runtime diagnostics.</summary>
    [Fact]
    public void ProductionSourcesContainNoTemporaryDiagnosticRuntimePath()
    {
        string modifier = File.ReadAllText(ResolveSourcePath());
        string anatomicalMath = File.ReadAllText(ResolveSourcePath("FingerAnatomicalMath.cs"));
        string referenceSampler = File.ReadAllText(ResolveSourcePath("AuthoredThumbReferenceSampler.cs"));
        string jointProvider = File.ReadAllText(ResolveSourcePath("IXRHandJointProvider.cs"));
        string openXR = File.ReadAllText(ResolveOpenXRSourcePath());

        foreach (string source in new[] { modifier, anatomicalMath, referenceSampler, jointProvider, openXR })
        {
            Assert.DoesNotContain("JSONL", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("runtime fitting", source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("temporary configurable stand-in", source, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("diagnostic-only", anatomicalMath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source observation", jointProvider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Guards the production contract.</summary>
    [Fact]
    public void RestNeutralBindingUsesOnlyDestinationGlobalRestGeometryAndRotationCache()
    {
        string modifier = File.ReadAllText(ResolveSourcePath());
        string helper = File.ReadAllText(ResolveSourcePath("FingerRestNeutralMath.cs"));

        Assert.Contains("TryDeriveRestNeutrals(skeleton", modifier, StringComparison.Ordinal);
        Assert.Contains("FingerAnatomicalMath.TryDeriveBilateralBinding(", modifier, StringComparison.Ordinal);
        Assert.Contains("_effectiveDestinationNeutrals", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Animation", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBonePose", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBonePosePosition", modifier, StringComparison.Ordinal);
        Assert.DoesNotContain("SetBonePoseScale", modifier, StringComparison.Ordinal);
    }

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected to locate '{startMarker}' in production source.");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected to locate '{endMarker}' after the region start.");
        return source[start..end];
    }

    private static string ReadProcessHandSource()
    {
        string source = File.ReadAllText(ResolveSourcePath());
        return ExtractRegion(source, "private void ProcessHand(", "private void FetchTrackedJoints(");
    }

    private static string ResolveSourcePath()
        => ResolveSourcePath("OpticalFingerTrackingModifier.cs");

    private static string ResolveOpenXRSourcePath()
        => ResolveProjectSourcePath("game", "src", "XR", "OpenXR", "OpenXROpticalHandTracking.cs");

    private static string ResolveMockXRSourcePath()
        => ResolveProjectSourcePath("game", "src", "XR", "Mock", "MockXRRuntimeNode.cs");

    private static string ResolveSourcePath(string fileName)
        => ResolveProjectSourcePath("game", "src", "XR", "HandTracking", fileName);

    private static string ResolveProjectSourcePath(params string[] pathSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. pathSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(pathSegments)} from the test output directory.");
    }
}

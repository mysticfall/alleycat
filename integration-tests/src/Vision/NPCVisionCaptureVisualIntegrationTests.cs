using AlleyCat.Vision;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Vision;

/// <summary>
/// AC78 visual-evidence producer: captures rendered NPC vision images from the real reference
/// female NPC template rig with the documented defaults — a facing person at conversational
/// distance, neutral and turned-head cases — and saves them under <c>temp/vision-capture/</c> for
/// inspection. Mechanism assertions guard the artefacts; the natural-glance framing judgement is
/// made by inspecting the saved images.
/// </summary>
public sealed class NPCVisionCaptureVisualIntegrationTests
{
    private const string ReferenceFemaleNpcScenePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string ReferenceMaleNpcScenePath =
        "res://assets/characters/templates/reference_male/reference_male_npc.tscn";
    private const string TestEnvironmentScenePath = "res://assets/testing/test_environment.tscn";
    private const string EvidenceDirectory = "res://temp/vision-capture";

    private const float ConversationalDistanceMetres = 2.4f;

    private const int StableSettleSampleCount = 5;
    private const float StableSettlePositionEpsilonMetres = 0.001f;
    private const float StableSettleRotationEpsilonDegrees = 0.1f;
    private const ulong SettleBudgetMilliseconds = 10_000;

    /// <summary>
    /// Produces the AC78 capture set: neutral glance plus ±30° turned-head captures of a facing
    /// person at ~2.4 m. Every scenario yields a distinct, correctly sized, non-empty image from
    /// the production capture path.
    /// </summary>
    [Fact]
    public async Task NpcVisionCapture_FacingPerson_ProducesInspectableConversationalGlanceImages()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "NpcVisionCaptureVisual"
        };
        root.AddChild(LoadPackedScene(TestEnvironmentScenePath).Instantiate());

        MeshInstance3D ground = new()
        {
            Name = "Ground",
            Mesh = new BoxMesh { Size = new Vector3(200f, 0.1f, 200f) },
            Position = new Vector3(0f, -0.05f, 0f),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.44f, 0.47f) },
        };
        root.AddChild(ground);

        CharacterBody3D observer = LoadPackedScene(ReferenceFemaleNpcScenePath).Instantiate<CharacterBody3D>();
        root.AddChild(observer);

        CharacterBody3D person = LoadPackedScene(ReferenceMaleNpcScenePath).Instantiate<CharacterBody3D>();
        person.ProcessMode = Node.ProcessModeEnum.Disabled;
        person.Position = new Vector3(0f, 0f, -ConversationalDistanceMetres);
        person.Basis = Basis.FromEuler(new Vector3(0f, Mathf.Pi, 0f));
        root.AddChild(person);

        // Let the observer settle before freezing it for deterministic evidence captures: the
        // skeleton must drive its bone attachments into the settled pose (the authored attachment
        // transform alone is not the bind pose), and a fixed frame count can catch the skeleton
        // mid-settle under full-suite load. The poll steps physics ticks — the rig's skeleton
        // solvers settle there, and any process-driven change is still observable at the next
        // physics sample — and freezes the observer only after both tracked transforms have held
        // still for several consecutive samples within a bounded wall-clock budget.
        await AddChildToRootAsync(sceneTree, root);
        await WaitForRigSettleAsync(sceneTree, observer, person);
        observer.ProcessMode = Node.ProcessModeEnum.Disabled;
        await WaitForFramesAsync(sceneTree, 1);
        try
        {
            EyesBehaviour eyes = observer.GetNode<EyesBehaviour>("Eyes");
            Node3D observerEyeOrigin = eyes.EyeOrigin
                ?? throw new InvalidOperationException("Expected the NPC template to author an eye origin.");
            Node3D personViewpoint = person.GetNode<Node3D>("Male/GeneralSkeleton/Head/Viewpoint");

            // Directional sanity before trusting any screenshot: the observer looks down -Z, the
            // person stands ~2.4 m ahead and faces back towards her.
            Vector3 toPerson = personViewpoint.GlobalPosition - observerEyeOrigin.GlobalPosition;
            Assert.True(
                toPerson.Dot(-observerEyeOrigin.GlobalBasis.Z.Normalized()) > toPerson.Length() * 0.94f,
                $"Expected the person to stand straight ahead of the observer (offset {toPerson}).");
            Assert.InRange(toPerson.Length(), ConversationalDistanceMetres - 0.5f, ConversationalDistanceMetres + 0.5f);
            Assert.True(
                (-personViewpoint.GlobalBasis.Z.Normalized()).Dot(-toPerson.Normalized()) > 0.94f,
                "Expected the person's viewpoint forward to face back towards the observer.");

            // SavePng does not create missing parent directories; the evidence directory is
            // gitignored, so the test must recreate it from any clean checkout.
            _ = DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(EvidenceDirectory));

            Image neutral = await eyes.CaptureImage().AsTask();
            AssertCaptureLooksRendered(neutral, "neutral");
            Assert.Equal(Error.Ok, neutral.SavePng($"{EvidenceDirectory}/npc_neutral.png"));

            foreach (float yawDegrees in new[] { 30f, -30f })
            {
                observer.Basis = Basis.FromEuler(new Vector3(0f, Mathf.DegToRad(yawDegrees), 0f));
                Image turned = await eyes.CaptureImage().AsTask();
                AssertCaptureLooksRendered(turned, $"turned {yawDegrees:F0}°");
                Assert.NotEqual(HashImage(neutral), HashImage(turned));
                Assert.Equal(Error.Ok, turned.SavePng($"{EvidenceDirectory}/npc_turn_{(yawDegrees > 0 ? "plus" : "minus")}30.png"));
            }

            Image neutralRepeat = await eyes.CaptureImage().AsTask();
            Assert.NotEqual(HashImage(neutral), HashImage(neutralRepeat));
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Advances physics ticks until the observer's eye origin and the person's viewpoint hold their
    /// global transforms stable for several consecutive samples, or fails when the settle budget is
    /// exhausted — never a fixed frame count, which can catch the skeleton mid-settle under load.
    /// </summary>
    private static async Task WaitForRigSettleAsync(SceneTree sceneTree, Node3D observer, Node3D person)
    {
        EyesBehaviour eyes = observer.GetNode<EyesBehaviour>("Eyes");
        Node3D eyeOrigin = eyes.EyeOrigin
            ?? throw new InvalidOperationException("Expected the NPC template to author an eye origin.");
        Node3D personViewpoint = person.GetNode<Node3D>("Male/GeneralSkeleton/Head/Viewpoint");

        Transform3D previousEye = eyeOrigin.GlobalTransform;
        Transform3D previousPerson = personViewpoint.GlobalTransform;
        float lastEyePositionDelta = 0f;
        float lastEyeRotationDelta = 0f;
        float lastPersonPositionDelta = 0f;
        float lastPersonRotationDelta = 0f;
        int stableSamples = 1;
        ulong startMilliseconds = Time.GetTicksMsec();

        while (stableSamples < StableSettleSampleCount)
        {
            if (Time.GetTicksMsec() - startMilliseconds > SettleBudgetMilliseconds)
            {
                throw new InvalidOperationException(
                    $"The observer rig did not settle within {SettleBudgetMilliseconds} ms; last per-sample deltas "
                    + $"— eye {lastEyePositionDelta:F4} m/{lastEyeRotationDelta:F3}°, person {lastPersonPositionDelta:F4} m/"
                    + $"{lastPersonRotationDelta:F3}° (limits {StableSettlePositionEpsilonMetres:F3} m/"
                    + $"{StableSettleRotationEpsilonDegrees:F1}°).");
            }

            await WaitForNextPhysicsFrameAsync(sceneTree);

            Transform3D currentEye = eyeOrigin.GlobalTransform;
            Transform3D currentPerson = personViewpoint.GlobalTransform;
            lastEyePositionDelta = previousEye.Origin.DistanceTo(currentEye.Origin);
            lastEyeRotationDelta = RotationDifferenceDegrees(previousEye.Basis, currentEye.Basis);
            lastPersonPositionDelta = previousPerson.Origin.DistanceTo(currentPerson.Origin);
            lastPersonRotationDelta = RotationDifferenceDegrees(previousPerson.Basis, currentPerson.Basis);

            bool stable = lastEyePositionDelta <= StableSettlePositionEpsilonMetres
                && lastEyeRotationDelta <= StableSettleRotationEpsilonDegrees
                && lastPersonPositionDelta <= StableSettlePositionEpsilonMetres
                && lastPersonRotationDelta <= StableSettleRotationEpsilonDegrees;
            stableSamples = stable ? stableSamples + 1 : 1;
            previousEye = currentEye;
            previousPerson = currentPerson;
        }
    }

    private static float RotationDifferenceDegrees(Basis previous, Basis current)
    {
        Quaternion previousRotation = new(previous.Orthonormalized());
        Quaternion currentRotation = new(current.Orthonormalized());
        float absoluteDot = Mathf.Clamp(Mathf.Abs(previousRotation.Dot(currentRotation)), 0.0f, 1.0f);
        return Mathf.RadToDeg(2.0f * MathF.Acos(absoluteDot));
    }

    private static void AssertCaptureLooksRendered(Image image, string scenario)
    {
        Assert.False(image.IsEmpty(), $"Scenario '{scenario}' produced an empty capture.");
        Assert.Equal(768, image.GetWidth());
        Assert.Equal(768, image.GetHeight());
        Assert.False(IsUniformColour(image), $"Scenario '{scenario}' produced a uniform single-colour image.");
    }

    private static bool IsUniformColour(Image image)
    {
        Color first = image.GetPixel(0, 0);
        for (int y = 0; y < image.GetHeight(); y += 16)
        {
            for (int x = 0; x < image.GetWidth(); x += 16)
            {
                if (!image.GetPixel(x, y).IsEqualApprox(first))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string HashImage(Image image)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image.GetData()));

    private static async Task AddChildToRootAsync(SceneTree sceneTree, Node child)
    {
        _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, child);
        await WaitForNextFrameAsync(sceneTree);
        Assert.True(child.IsInsideTree(), $"Expected '{child.Name}' to enter the test scene tree.");
    }
}

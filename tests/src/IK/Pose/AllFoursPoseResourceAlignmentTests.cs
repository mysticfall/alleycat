using System.Globalization;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Verifies the shipped all-fours resources keep their shared forward-reference baseline aligned.
/// </summary>
public sealed class AllFoursPoseResourceAlignmentTests
{
    private const string ReferenceForwardShiftRatioPropertyName = "ReferenceForwardShiftRatio";
    private const string AllFoursPoseStateResourceRelativePath = "game/assets/characters/ik/pose/all_fours_pose_state.tres";
    private static readonly string[] _allFoursTransitionResourceRelativePaths =
    [
        "game/assets/characters/ik/pose/standing_to_all_fours_transition.tres",
        "game/assets/characters/ik/pose/kneeling_to_all_fours_transition.tres",
        "game/assets/characters/ik/pose/all_fours_to_standing_transition.tres",
    ];

    /// <summary>
    /// Every shipped all-fours transition resource must mirror the all-fours pose-state reference forward shift.
    /// </summary>
    [Fact]
    public void ShippedAllFoursTransitions_KeepReferenceForwardShiftRatioAlignedWithPoseState()
    {
        string repositoryRootPath = ResolveRepositoryRootPath();
        float poseStateReferenceForwardShiftRatio = ReadRequiredFloatProperty(
            GetRepositoryPath(repositoryRootPath, AllFoursPoseStateResourceRelativePath),
            ReferenceForwardShiftRatioPropertyName);

        foreach (string transitionResourceRelativePath in _allFoursTransitionResourceRelativePaths)
        {
            string transitionResourcePath = GetRepositoryPath(repositoryRootPath, transitionResourceRelativePath);
            float transitionReferenceForwardShiftRatio = ReadRequiredFloatProperty(
                transitionResourcePath,
                ReferenceForwardShiftRatioPropertyName);

            Assert.True(
                Math.Abs(poseStateReferenceForwardShiftRatio - transitionReferenceForwardShiftRatio) <= 1e-6f,
                $"Expected '{transitionResourceRelativePath}' {ReferenceForwardShiftRatioPropertyName} ({transitionReferenceForwardShiftRatio}) " +
                $"to match '{AllFoursPoseStateResourceRelativePath}' ({poseStateReferenceForwardShiftRatio}).");
        }
    }

    private static float ReadRequiredFloatProperty(string resourcePath, string propertyName)
    {
        Assert.True(File.Exists(resourcePath), $"Expected resource file '{resourcePath}' to exist.");

        string propertyPrefix = propertyName + " = ";
        string? propertyLine = File.ReadLines(resourcePath)
            .Select(line => line.Trim())
            .SingleOrDefault(line => line.StartsWith(propertyPrefix, StringComparison.Ordinal));

        Assert.False(
            string.IsNullOrWhiteSpace(propertyLine),
            $"Expected resource file '{resourcePath}' to declare '{propertyName}'.");

        string propertyValue = propertyLine![propertyPrefix.Length..];
        Assert.True(
            float.TryParse(propertyValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue),
            $"Expected '{propertyName}' in '{resourcePath}' to contain an invariant-culture float, but found '{propertyValue}'.");

        return parsedValue;
    }

    private static string GetRepositoryPath(string repositoryRootPath, string relativePath)
        => Path.Combine(repositoryRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string ResolveRepositoryRootPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
}

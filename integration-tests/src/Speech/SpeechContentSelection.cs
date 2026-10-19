using AlleyCat.Core.Content;
using Xunit;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>Representative explicit startup selection using real per-pack metadata, shared by both speech paths.</summary>
internal static class SpeechContentSelection
{
    internal static ContentContext Select(string language)
    {
        string requestedPack = language + "-pack";
        ContentContext selected = ContentResolver.SelectCurrentContentContext(
            requestedPack,
            language == "en" ? "ko-pack" : "en-pack",
            isIntegrationTest: false,
            skipContentPack: false,
            sceneExists: path => path == $"res://content/{requestedPack}/start.tscn",
            resolvePackLanguage: pack => ContentResolver.LoadPackLanguage(
                pack,
                "res://assets/testing/content/pack-manifests/" + (language == "ko" ? "korean" : "regional") + ".tres"));
        Assert.Equal(requestedPack, selected.ContentID);
        Assert.Equal(language, selected.Language);
        return selected;
    }
}

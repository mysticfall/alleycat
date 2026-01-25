using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Lore.Markdown;

[GlobalClass]
public partial class MarkdownLoreBookFactory : LoreBookFactory
{
    [Export(PropertyHint.Dir)] public string? ContentRoot { get; set; }

    protected override Eff<IEnv, ILoreBook> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from path in Io.ResourcePath.Create(ContentRoot).ToEff(identity)
        from book in MarkdownLoreBook.Create(path, loggerFactory)
        select book;
}
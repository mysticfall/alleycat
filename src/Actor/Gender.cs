namespace AlleyCat.Actor;

public enum Gender
{
    Male,
    Female,
    Other
}

public readonly struct Pronouns(
    string subject,
    string objective,
    string possessiveAdj,
    string possessiveNoun,
    string reflexive
)
{
    public string Subject { get; } = subject;
    public string Object { get; } = objective;
    public string PossessiveAdj { get; } = possessiveAdj;
    public string PossessiveNoun { get; } = possessiveNoun;
    public string Reflexive { get; } = reflexive;

    public static Pronouns For(Gender gender) => gender switch
    {
        Gender.Male => new Pronouns("he", "him", "his", "his", "himself"),
        Gender.Female => new Pronouns("she", "her", "her", "hers", "herself"),
        _ => new Pronouns("they", "them", "their", "theirs", "themself")
    };
}

public interface IGendered
{
    Gender Gender { get; }

    Pronouns Pronouns => Pronouns.For(Gender);
}
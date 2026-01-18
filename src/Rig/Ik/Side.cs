using System.Text;

namespace AlleyCat.Rig.Ik;

public enum Side
{
    Right,
    Left
}

public static class SideExtensions
{
    public static string PrefixWith(this string name, Side side)
    {
        var prefix = side switch
        {
            Side.Right => "Right",
            Side.Left => "Left",
            _ => throw new ArgumentOutOfRangeException(nameof(side), side, null)
        };

        var builder = new StringBuilder(prefix);

        if (!string.IsNullOrEmpty(name))
        {
            builder.Append(char.ToUpper(name[0]));
            builder.Append(name[1..]);
        }

        return builder.ToString();
    }
}
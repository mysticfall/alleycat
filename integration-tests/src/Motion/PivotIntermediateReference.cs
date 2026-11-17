using Godot;

namespace AlleyCat.IntegrationTests.Motion;

internal static class PivotIntermediateReference
{
    internal static void AddGround(Node root)
    {
        void Box(Vector3 size, Vector3 position, Color colour)
        {
            var node = new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = size },
                Position = position,
                MaterialOverride = new StandardMaterial3D { AlbedoColor = colour }
            };
            root.AddChild(node);
        }
        Box(new Vector3(8, 0.01f, 8), new Vector3(0, -0.006f, 0), new Color(0.55f, 0.55f, 0.55f));
        for (int i = -16; i <= 16; i++)
        {
            Box(new Vector3(8, 0.001f, 0.003f), new Vector3(0, 0, i * 0.25f), new Color(0.25f, 0.25f, 0.25f));
            Box(new Vector3(0.003f, 0.001f, 8), new Vector3(i * 0.25f, 0, 0), new Color(0.25f, 0.25f, 0.25f));
        }
        Box(new Vector3(0.025f, 0.003f, 0.5f), new Vector3(0, 0.002f, -0.7f), Colors.Blue);
        root.AddChild(new Label3D
        {
            Text = "WORLD -Z / INITIAL FRONT",
            Position = new Vector3(0, 0.04f, -1),
            FontSize = 32,
            PixelSize = 0.002f,
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled
        });
    }
}

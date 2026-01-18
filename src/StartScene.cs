using Godot;

namespace AlleyCat;

public partial class StartScene : Node3D
{
    [Export] public XRController3D? RightController { get; set; }

    [Export] public XRController3D? LeftController { get; set; }

    [Export] public PackedScene? MainScene { get; set; }

    public override void _Ready()
    {
        base._Ready();

        if (RightController != null)
        {
            RightController.ButtonPressed += OnButtonPressed;
        }
    }

    private void OnButtonPressed(string name)
    {
        if (name == "ax_button" && MainScene != null)
        {
            GetTree().ChangeSceneToPacked(MainScene);
        }

        GD.Print(name);
    }
}
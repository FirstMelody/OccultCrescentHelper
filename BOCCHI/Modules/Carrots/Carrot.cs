using System.Numerics;
namespace BOCCHI.Modules.Carrots;

public class Carrot(Vector3 position)
{
    public static Vector4 Color { get; } = new(0.2f, 0.8f, 0.2f, 1f);

    public bool IsValid()
    {
        return true;
    }

    public Vector3 GetPosition()
    {
        return position;
    }
}

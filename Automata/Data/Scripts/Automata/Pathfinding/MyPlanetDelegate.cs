using Sandbox.Game.Entities;
using VRageMath;

/// <summary>
/// Delegate interface for MyPlanet operations
/// </summary>
public interface IMyPlanetDelegate
{
    MyPlanet GetClosestPlanet(Vector3D position);
    double GetSurfaceAltitude(Vector3D position, MyPlanet planet);
}

/// <summary>
/// Production implementation using real SE API
/// </summary>
public class MyPlanetDelegate : IMyPlanetDelegate
{
    public MyPlanet GetClosestPlanet(Vector3D position)
    {
        return MyGamePruningStructure.GetClosestPlanet(position);
    }

    public double GetSurfaceAltitude(Vector3D position, MyPlanet planet)
    {
        if (planet == null)
            return double.MaxValue;

        var planetCenter = planet.PositionComp.GetPosition();
        var distanceFromCenter = Vector3D.Distance(position, planetCenter);
        var surfaceAltitude = distanceFromCenter - planet.AverageRadius;

        return surfaceAltitude;
    }
}
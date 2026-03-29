namespace zRover.Core.Coordinates
{
    public interface ICoordinateResolver
    {
        CoordinatePoint Resolve(CoordinatePoint point, CoordinateSpace space);
    }
}
